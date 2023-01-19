using NBitcoin;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BrainwalletFinder
{
	internal class ScanEngine
	{
		private static readonly Regex GetWordsRegex = new Regex(@"\b\w+\b\p{P}*", RegexOptions.Compiled);
		private const int MaxSentenceLength = 20;
		private const string FoundFile = "Found.txt";
		private static readonly object FileLock = new object();

		private readonly Dictionary<int, ConcurrentQueue<string[]>> _queueMap;
		private readonly HashSet<string> _hashes;
		private readonly HashSet<string> _segwit;
		private readonly CancellationTokenSource _cts;
		private readonly Task[] _tasks;

		private long _found;
		private long _progress;
		private long _maxProgress;

		public ScanEngine()
		{
			_queueMap = new Dictionary<int, ConcurrentQueue<string[]>>();
			_hashes = new HashSet<string>();
			_segwit = new HashSet<string>();
			_cts = new CancellationTokenSource();
			_tasks = new Task[Environment.ProcessorCount];
		}

		public bool IsCompleted
		{
			get
			{
				return _tasks.All(
					task => task != null && task.IsCompleted);
			}
		}

		public (double Current, long Found) GetProgress()
		{
			long progress = Interlocked.Read(ref _progress);
			long found = Interlocked.Read(ref _found);

			double current = Math.Round(100.0d * ((double) progress / _maxProgress), 2);

			return (current, found);
		}

		public void ReadWallets(TextReader file)
		{
			Console.WriteLine(">>> Loading Bitcoin hashes/wallets...");

			string wallet;
			while ((wallet = file.ReadLine()) != null)
			{
				wallet = wallet.Trim();
				if (wallet.Length == 0)
					continue;

				if (wallet.StartsWith("1"))
				{
					var addr = new BitcoinPubKeyAddress(wallet, Network.Main);
					_hashes.Add(addr.Hash.ToString());
					continue;
				}

				if (wallet.StartsWith("3"))
				{
					var addr = new BitcoinScriptAddress(wallet, Network.Main);
					_hashes.Add(addr.Hash.ToString());
					continue;
				}

				if (wallet.StartsWith("bc1"))
				{
					_segwit.Add(wallet);
					continue;
				}
			}

			// trim excess for max performance
			_hashes.TrimExcess();
			_segwit.TrimExcess();

			int total = _hashes.Count + _segwit.Count;
			Console.WriteLine($">>> Count of Bitcoin hashes/wallets loaded: {total:N0}");
		}
		
		public void Run(FileInfo file)
		{
			ReadWordTokens(file);

			Console.WriteLine($">>> Starting {_tasks.Length:N0} engine threads...");

			for (int iTask = 0; iTask < _tasks.Length; iTask++)
			{
				Task task = new Task(ScanRunner, TaskCreationOptions.LongRunning);
				_tasks[iTask] = task;
				task.Start();
			}
		}

		public void Stop()
		{
			_cts.Cancel();

			Task[] onTasks = _tasks.Where(task => task != null && !task.IsCompleted).ToArray();
			if (onTasks.Length > 0)
			{
				Task.WaitAll(onTasks);
			}
		}

		public bool StopRequested
		{
			get
			{
				return _cts.IsCancellationRequested;
			}
		}

		private void ScanRunner()
		{
			CancellationToken ct = _cts.Token;

			while (!ct.IsCancellationRequested)
			{
				string[] tokens = GetNextTokens();
				if (tokens == null)
					break;

				string TEXT = string.Join("\x20", tokens);

				HashResult hashResult = Hasher.GetHash(TEXT);
				
				CheckHash(hashResult.Hash);
				CheckHash(hashResult.RevHash);
				CheckHash(hashResult.ReHash);

				Interlocked.Increment(ref _progress);
			}
		}

		private void CheckHash(byte[] hash)
		{
			var keys = GetKeys(hash);

			if (keys.Success)
			{
				RunCheck(keys.Compressed);
				RunCheck(keys.UnCompressed);
			}
		}

		private static (bool Success, Key Compressed, Key UnCompressed) GetKeys(byte[] data)
		{
			try
			{
				Key keyC = new Key(data, fCompressedIn: true);
				Key keyU = new Key(data, fCompressedIn: false);

				return (Success: true, Compressed: keyC, UnCompressed: keyU);
			}
			catch (Exception)
			{
				return (Success: false, Compressed: null, UnCompressed: null);
			}
		} 

		private string[] GetNextTokens()
		{
			string[] nextTokens = null;

			foreach (int key in _queueMap.Keys)
			{
				var queue = _queueMap[key];
				if (queue.TryDequeue(out nextTokens))
					break;
			}

			return nextTokens;
		}

		private void ReadWordTokens(FileInfo file)
		{
			// clear previous state
			_queueMap.Clear();
			_progress = 0L;
			_maxProgress = 0L;

			Console.WriteLine($">>> Extracting words from the file [{file.Name}]...");
			var tokens = new List<string>();

			using (StreamReader text = file.OpenText())
			{
				string line;
				while ((line = text.ReadLine()) != null)
				{
					line = line.Trim();
					if (line.Length == 0)
						continue;

					tokens.AddRange(
						GetWordsRegex.Matches(line)
						.Cast<Match>()
						.Select(x => x.Value));
				}
			}

			Console.WriteLine($">>> Extracted {tokens.Count:N0} words from the file [{file.Name}]");

			for (int length = 1; length <= MaxSentenceLength; length++)
			{
				ConcurrentQueue<string[]> queue = new ConcurrentQueue<string[]>();
				PopulateQueue(queue, tokens, length);

				_queueMap.Add(length, queue);
				_maxProgress += queue.Count;
			}
		}

		private static void PopulateQueue(ConcurrentQueue<string[]> queue, List<string> tokens, int length)
		{
			for (int index = 0; index < tokens.Count; index++)
			{
				string[] next = tokens.Skip(index).Take(length).ToArray();

				if (next.Length == length)
				{
					queue.Enqueue(next);
				}
			}
		}

		private void RunCheck(Key key)
		{
			if (_hashes.Contains(key.PubKey.Hash.ToString()))
			{
				SavePrivateKey(key);
				Interlocked.Increment(ref _found);
			}

			if (key.IsCompressed)
			{
				// SegWit address
				BitcoinAddress address = key.GetAddress(ScriptPubKeyType.Segwit, Network.Main);
				if (_segwit.Contains(address.ToString()))
				{
					SavePrivateKey(key);
					Interlocked.Increment(ref _found);
				}
			}
		}

		private static void SavePrivateKey(Key key)
		{
			lock (FileLock)
			{
				using (StreamWriter file = File.AppendText(Path.Combine(AppContext.BaseDirectory, FoundFile)))
				{
					file.WriteLine($"Address (Legacy): {key.GetAddress(ScriptPubKeyType.Legacy, Network.Main)}");
					file.WriteLine($"Address (SegWit): {key.GetAddress(ScriptPubKeyType.Segwit, Network.Main)}");
					file.WriteLine($"Address (SegWit-P2SH): {key.GetAddress(ScriptPubKeyType.SegwitP2SH, Network.Main)}");
					file.WriteLine($"Compressed: {key.IsCompressed.ToString().ToLowerInvariant()}");

					file.WriteLine("Private key:");
					    file.WriteLine($">>> HEX: {key.ToHex()}");
					    file.WriteLine($">>> WIF: {key.GetBitcoinSecret(Network.Main).ToWif()}");

					file.WriteLine();
					file.Flush();
				}
			}
		}
	}
}