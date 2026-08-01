using System;

namespace XtermSharp {
	/// <summary>
	/// Buffer for processing input
	/// </summary>
	/// <remarks>
	/// Because data might not be complete, we need to put back data that we read to process on
	/// a future read.  To prepare for reading, on every call to parse, the prepare method is
	/// given the new buffer to read from.
	///
	/// the `hasNext` describes whether there is more data left on the buffer, and `bytesLeft`
	/// returnes the number of bytes left.  The `getNext` method fetches either the next
	/// value from the putback buffer, or when it is empty, it returns it from the buffer that
	/// was passed during prepare.
	///
	/// Additionally, the terminal parser needs to reset the parser state on demand, and
	/// that is surfaced via reset
	/// </remarks>
	class ReadingBuffer {
		// Putback bytes are stored in a fixed ring instead of a freshly
		// allocated array per Putback call: a multi-byte rune split across two
		// terminal feeds hits Putback on almost every Print run and the old
		// copy-per-byte path burned allocations on the parse hot path.
		readonly byte [] putbackBuffer = new byte [16];
		int putbackStart;
		int putbackCount;

		unsafe byte* buffer;
		int bufferStart;
		int totalCount;
		int index;

		unsafe public void Prepare (byte* data, int start, int length)
		{
			buffer = data;
			bufferStart = start;

			index = 0;
			totalCount = putbackCount + length;
		}

		public int BytesLeft ()
		{
			return totalCount - index;
		}

		public bool HasNext ()
		{
			return index < totalCount;
		}

		unsafe public byte GetNext ()
		{
			byte val;
			if (index < putbackCount) {
				// grab from putback buffer
				val = putbackBuffer [(putbackStart + index) % putbackBuffer.Length];
			} else {
				// grab from the prepared buffer
				val = buffer [bufferStart + (index - putbackCount)];
			}

			index++;
			return val;
		}

		/// <summary>
		/// Puts back code and the remainder of the buffer
		/// </summary>
		public void Putback (byte code)
		{
			var left = BytesLeft ();
			// Roll the ring so the new byte lands first, then the previously
			// put-back bytes, then whatever remains of the prepared buffer.
			if (left + 1 > putbackBuffer.Length) {
				// Overflow is degenerate (a single rune is at most 4 bytes);
				// degrade to a temporary array rather than truncating data.
				var overflow = new byte [left + 1];
				overflow [0] = code;
				for (int i = 0; i < left; i++)
					overflow [i + 1] = GetNext ();
				Array.Copy (overflow, putbackBuffer, putbackBuffer.Length);
				putbackStart = 0;
				putbackCount = putbackBuffer.Length;
				return;
			}

			// Rotate existing putback bytes forward to make room at the head.
			var newStart = (putbackStart + putbackBuffer.Length - 1) % putbackBuffer.Length;
			for (int i = putbackCount - 1; i >= 0; i--) {
				var from = (putbackStart + i) % putbackBuffer.Length;
				var to = (newStart + i + 1) % putbackBuffer.Length;
				putbackBuffer [to] = putbackBuffer [from];
			}
			putbackBuffer [newStart] = code;
			putbackStart = newStart;
			putbackCount = Math.Min (putbackCount + 1, putbackBuffer.Length);
		}

		unsafe public void Done ()
		{
			// Consumed putback bytes are dropped by advancing the ring head.
			var consumed = Math.Min (index, putbackCount);
			if (consumed > 0) {
				putbackStart = (putbackStart + consumed) % putbackBuffer.Length;
				putbackCount -= consumed;
			}
			index = 0;

			buffer = null;
		}

		public void Reset ()
		{
			putbackStart = 0;
			putbackCount = 0;
			index = 0;
			unsafe { buffer = null; }
		}
	}
}
