using System;

namespace BrainwalletFinder
{
	internal static class Extensions
	{
		public static T[] Reverse<T>(this T[] data)
		{
			T[] result = data.Clone() as T[];
			Array.Reverse(result);
			return result;
		}
	}
}