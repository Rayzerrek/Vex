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

		// Lookup path for the renderer: the per-brush cache in the view is
		// consulted first, so this array is hit only on truecolor misses.
		static readonly Color [] trueColorArray = new Color [TrueColorEnd - TrueColorStart + 1];
		static readonly bool [] trueColorValid = new bool [TrueColorEnd - TrueColorStart + 1];

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
				trueColorArray [index - TrueColorStart] = new Color (red, green, blue);
				trueColorValid [index - TrueColorStart] = true;
				return index;
			}
		}

		public static bool TryGetTrueColor (int index, out Color color)
		{
			// Index is validated by the caller; reads are lock-free after the
			// register-time write, which is safe here because the write happens
			// under a lock and array element assignment is atomic for references
			// and aligned primitives.
			if ((uint)(index - TrueColorStart) < (uint)trueColorValid.Length && trueColorValid [index - TrueColorStart]) {
				color = trueColorArray [index - TrueColorStart];
				return true;
			}
			color = default (Color);
			return false;
		}
	}
}
