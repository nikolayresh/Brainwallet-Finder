using System.Security.Cryptography;
using System.Text;

namespace BrainwalletFinder
{
	internal static class Hasher
	{
		public static HashResult GetHash(string plainText)
		{
			var hr = new HashResult();

			hr.Hash = ComputeSHA256(plainText);
			hr.RevHash = hr.Hash.Reverse();
			hr.ReHash = ComputeSHA256(hr.Hash);

			return hr;
		}

		private static byte[] ComputeSHA256(string plainText)
		{
			byte[] textAsBytes = Encoding.UTF8.GetBytes(plainText);

			return ComputeSHA256(textAsBytes);
		}

		private static byte[] ComputeSHA256(byte[] data)
		{
			using (SHA256 sha = SHA256.Create())
			{
				return sha.ComputeHash(data);
			}
		}
	}
}