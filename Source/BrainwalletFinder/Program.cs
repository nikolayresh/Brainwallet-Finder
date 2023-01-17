using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace BrainwalletFinder
{
	internal class Program
	{
		private const string WalletsFile = "Wallets.txt";
		private const string BooksFolder = "Books";

		static void Main(string[] args)
		{
			Console.ForegroundColor = ConsoleColor.Green;

			ScanEngine engine = new ScanEngine();

			using (StreamReader file = File.OpenText(Path.Combine(AppContext.BaseDirectory, WalletsFile)))
			{
				// read Bitcoin wallets
				engine.ReadWallets(file);
			}

			Console.CancelKeyPress += (sender, e) =>
			{
				// stop the engine
				engine.Stop();
			};

			var folder = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, BooksFolder));

			foreach (FileInfo file in folder.EnumerateFiles()
				                  .Where(x => x.Extension == ".txt")
								  .OrderBy(x => x.Length))
			{
				Console.WriteLine();

				// start search for the next file
				engine.Run(file);

				while (!engine.IsCompleted)
				{
					var progress = engine.GetProgress();

					Console.Write($"\r>>> Progress: {progress.Current:F2}%; Found: {progress.Found:N0}");
					Thread.Sleep(TimeSpan.FromSeconds(5));
				}

				// get final progress
				var (Current, Found) = engine.GetProgress();
				Console.WriteLine($"\r>>> Progress: {Current:F2}%; Found: {Found:N0}");

				if (engine.StopRequested)
				{
					Console.WriteLine(">>> Finder is requested to stop");
					break;
				}
			}

			Console.WriteLine(">>> DONE!");
		}
	}
}