using System;
using System.Collections.Generic;

namespace XtermSharp {
	public class Renderer {
		public const int DefaultColor = 256;
		public const int InvertedDefaultColor = 257;
		public const int TrueColorStart = 258;
		public const int TrueColorEnd = 511;

		static readonly object trueColorLock = new object ();
		static readonly Dictionary<int, int> trueColorIndexes = new Dictionary<int, int> ();
		static readonly Dictionary<int, Color> trueColors = new Dictionary<int, Color> ();
		static int nextTrueColor = TrueColorStart;

		public Renderer ()
		{
		}

		public static int RegisterTrueColor (byte red, byte green, byte blue)
		{
			var rgb = red << 16 | green << 8 | blue;
			lock (trueColorLock) {
				if (trueColorIndexes.TryGetValue (rgb, out var existing))
					return existing;
				if (nextTrueColor > TrueColorEnd)
					return -1;

				var index = nextTrueColor++;
				trueColorIndexes [rgb] = index;
				trueColors [index] = new Color (red, green, blue);
				return index;
			}
		}

		public static bool TryGetTrueColor (int index, out Color color)
		{
			lock (trueColorLock)
				return trueColors.TryGetValue (index, out color);
		}
	}
}
