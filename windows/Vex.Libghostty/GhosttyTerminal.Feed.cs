using System.Text;

namespace Vex.Libghostty;

public sealed partial class GhosttyTerminal
{
    public void Feed(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Feed(bytes, 0, bytes.Length);
    }

    public unsafe void Feed(byte[] data, int offset, int count)
    {
        lock (_vtLock)
        {
            FeedLocked(data, offset, count);
        }
    }

    private unsafe void FeedLocked(byte[] data, int offset, int count)
    {
        if (count <= 0 || _disposed)
            return;

        // Fast path: no ESC byte, no query introducer, and no half-parsed
        // sequence pending, so the chunk goes straight to the emulator
        // without the pooled-array copy the filter below needs. Only `+q`
        // and `$q` pairs (ConPTY-stripped DCS payloads carry no ESC to key
        // off) force the slow path — lone `+`/`$` (`a+b`, `$env:`) would pass
        // through the filter unchanged anyway. A trailing `+`/`$` also takes
        // the slow path so a pair split across Feed calls is still caught.
        if (_feedState == FeedState.Normal && !NeedsSlowPath(data, offset, count))
        {
            fixed (byte* p = data)
                Native.ghostty_terminal_vt_write(_terminal, (IntPtr)(p + offset), (nuint)count);
            return;
        }

        // Strip OSC 133 sequences (see the filter state above) before feeding,
        // tolerating sequences that are split across separate Feed calls.
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(count + _oscBuf.Length + 4);
        try
        {
            var written = 0;
            for (var i = offset; i < offset + count; i++)
            {
                var b = data[i];
                switch (_feedState)
                {
                    case FeedState.Normal:
                        if (b == 0x1B)
                            _feedState = FeedState.EscapeSeen;
                        else if (b == (byte)'+')
                            _feedState = FeedState.PlusSeen;
                        else if (b == (byte)'$')
                            _feedState = FeedState.DollarSeen;
                        else
                            buffer[written++] = b;
                        break;
                    case FeedState.EscapeSeen:
                        if (b == 0x5D) // ESC ] starts an OSC sequence
                        {
                            _feedState = FeedState.InOsc;
                            _oscLen = 0;
                        }
                        else if (b == (byte)'P' || b == (byte)'X' || b == (byte)'^' || b == (byte)'_')
                        {
                            // DCS/SOS/PM/APC: arbitrary payload that may itself
                            // contain `+q`/`$q` (XTGETTCAP, DECRQSS). Pass it
                            // through untouched until its terminator so the
                            // bare-payload matcher below never fires inside a
                            // real sequence.
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.InDcs;
                        }
                        else if (b == (byte)'[')
                        {
                            // CSI: params/intermediates may contain `+`/`$`
                            // (DECRQM `$p`); same passthrough requirement.
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.InCsi;
                        }
                        else
                        {
                            if (b == (byte)'c')
                                _programStatus.ResetProgramStatus();
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        break;
                    case FeedState.InOsc:
                        if (b == 0x07) // BEL terminates the OSC string
                        {
                            FlushOscBel(buffer, ref written);
                            _feedState = FeedState.Normal;
                        }
                        else if (b == 0x1B) // possible ST (ESC \) terminator
                        {
                            _feedState = FeedState.InOscEscapeSeen;
                        }
                        else if (_oscLen < _oscBuf.Length)
                        {
                            _oscBuf[_oscLen++] = b;
                        }
                        else
                        {
                            if (_oscBuf.AsSpan(0, _oscLen).StartsWith("52;"u8))
                            {
                                if (_oscBuf.Length < MaxClipboardOscBytes)
                                {
                                    Array.Resize(ref _oscBuf, Math.Min(_oscBuf.Length * 2, MaxClipboardOscBytes));
                                    _oscBuf[_oscLen++] = b;
                                }
                                else
                                    _feedState = FeedState.DropOsc;
                                break;
                            }
                            // Oversized status reports are discarded through their terminator.
                            if (_oscBuf.AsSpan(0, _oscLen).StartsWith("7501;"u8))
                            {
                                _feedState = FeedState.DropOsc;
                                break;
                            }
                            buffer[written++] = 0x1B;
                            buffer[written++] = 0x5D;
                            for (var j = 0; j < _oscLen; j++)
                                buffer[written++] = _oscBuf[j];
                            buffer[written++] = b;
                            _feedState = FeedState.InDcs;
                        }
                        break;
                    case FeedState.DropOsc:
                        if (b == 0x07) _feedState = FeedState.Normal;
                        else if (b == 0x1B) _feedState = FeedState.DropOscEscapeSeen;
                        break;
                    case FeedState.DropOscEscapeSeen:
                        _feedState = b is 0x5C or 0x07 ? FeedState.Normal : b == 0x1B ? FeedState.DropOscEscapeSeen : FeedState.DropOsc;
                        break;
                    case FeedState.InOscEscapeSeen:
                        if (b == 0x07)
                        {
                            if (_oscLen < _oscBuf.Length) _oscBuf[_oscLen++] = 0x1B;
                            FlushOscBel(buffer, ref written);
                            _feedState = FeedState.Normal;
                        }
                        else if (b == 0x5C) // ST terminator: ESC \
                        {
                            FlushOscSt(buffer, ref written);
                            _feedState = FeedState.Normal;
                        }
                        else
                        {
                            // A lone ESC inside the OSC string, not a terminator.
                            if (_oscLen < _oscBuf.Length)
                                _oscBuf[_oscLen++] = 0x1B;
                            if (b != 0x1B && _oscLen < _oscBuf.Length)
                                _oscBuf[_oscLen++] = b;
                            _feedState = b == 0x1B ? FeedState.InOscEscapeSeen : FeedState.InOsc;
                        }
                        break;
                    case FeedState.PlusSeen:
                        if (b == (byte)'q')
                        {
                            _feedState = FeedState.InPlusQuery;
                            _plusLen = 0;
                        }
                        else
                        {
                            // Ordinary text (`a+b`, `C++`): emit the held `+`
                            // and reprocess this byte as Normal.
                            buffer[written++] = (byte)'+';
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.InPlusQuery:
                        if (IsQueryByte(b) && _plusLen < _plusBuf.Length)
                        {
                            _plusBuf[_plusLen++] = b;
                        }
                        else if (_plusLen >= _plusBuf.Length)
                        {
                            // Pathological run: fail open and emit verbatim.
                            buffer[written++] = (byte)'+';
                            buffer[written++] = (byte)'q';
                            for (var j = 0; j < _plusLen; j++)
                                buffer[written++] = _plusBuf[j];
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        else if (IsStrippedQuery(_plusBuf, _plusLen))
                        {
                            // A ConPTY-stripped XTGETTCAP probe: drop it, then
                            // reprocess the terminating byte as Normal (it is
                            // usually the ESC of whatever the app sent next).
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        else
                        {
                            // Not a known probe: emit everything verbatim.
                            buffer[written++] = (byte)'+';
                            buffer[written++] = (byte)'q';
                            for (var j = 0; j < _plusLen; j++)
                                buffer[written++] = _plusBuf[j];
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.DollarSeen:
                        if (b == (byte)'q')
                        {
                            _feedState = FeedState.DollarQSeen;
                        }
                        else
                        {
                            // Ordinary text (`$env:`, `$100`): emit the held
                            // `$` and reprocess this byte as Normal.
                            buffer[written++] = (byte)'$';
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.DollarQSeen:
                        if (b == (byte)'m')
                        {
                            // nvim's undercurl DECRQSS probe (`ESC P $ q m`)
                            // without its DCS wrapper: drop it. Anything else
                            // after `$q` (shell `$query`, …) is text.
                            _feedState = FeedState.Normal;
                        }
                        else
                        {
                            buffer[written++] = (byte)'$';
                            buffer[written++] = (byte)'q';
                            _feedState = FeedState.Normal;
                            i--;
                        }
                        break;
                    case FeedState.InCsi:
                        if (b == 0x1B)
                        {
                            // ESC aborts CSI and starts a new sequence.
                            _feedState = FeedState.EscapeSeen;
                        }
                        else
                        {
                            buffer[written++] = b;
                            if (b >= 0x40 && b <= 0x7E)
                                _feedState = FeedState.Normal;
                        }
                        break;
                    case FeedState.InDcs:
                        if (b == 0x07) // BEL terminates some DCS uses
                        {
                            buffer[written++] = b;
                            _feedState = FeedState.Normal;
                        }
                        else if (b == 0x1B) // possible ST (ESC \) terminator
                        {
                            _feedState = FeedState.InDcsEsc;
                        }
                        else
                        {
                            buffer[written++] = b;
                        }
                        break;
                    case FeedState.InDcsEsc:
                        if (b == 0x5C) // ST terminator: ESC \
                        {
                            buffer[written++] = 0x1B;
                            buffer[written++] = 0x5C;
                            _feedState = FeedState.Normal;
                        }
                        else
                        {
                            // A lone ESC inside the payload, not a terminator.
                            buffer[written++] = 0x1B;
                            buffer[written++] = b;
                            _feedState = FeedState.InDcs;
                        }
                        break;
                }
            }
            if (written > 0)
                Native.ghostty_terminal_vt_write(_terminal, buffer, (nuint)written);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            // Large clipboard writes must not inflate every later output chunk's pooled buffer.
            if (_feedState == FeedState.Normal && _oscBuf.Length > 4096)
                _oscBuf = new byte[4096];
        }
    }

    /// <summary>
    /// Re-emits a buffered OSC sequence (with its terminator) unless it is an
    /// program status or semantic-prompt marker, consumed outside the grid. BEL and ST
    /// are the only terminators the feed filter emits, so the terminator
    /// bytes are inlined instead of allocating a params array.
    /// </summary>
    private void FlushOscBel(byte[] dst, ref int written)
    {
        ReadWorkingDirectoryOsc();
        if (ReadClipboardOsc() || ReadProgramStatusOsc(1))
            return;
        dst[written++] = 0x1B;
        dst[written++] = 0x5D;
        for (var j = 0; j < _oscLen; j++)
            dst[written++] = _oscBuf[j];
        dst[written++] = 0x07;
    }

    private void FlushOscSt(byte[] dst, ref int written)
    {
        ReadWorkingDirectoryOsc();
        if (ReadClipboardOsc() || ReadProgramStatusOsc(2))
            return;
        dst[written++] = 0x1B;
        dst[written++] = 0x5D;
        for (var j = 0; j < _oscLen; j++)
            dst[written++] = _oscBuf[j];
        dst[written++] = 0x1B;
        dst[written++] = 0x5C;
    }

    private bool ReadClipboardOsc()
    {
        var osc = _oscBuf.AsSpan(0, _oscLen);
        if (!osc.StartsWith("52;"u8))
            return false;
        var body = osc[3..];
        var separator = body.IndexOf((byte)';');
        if (separator < 0)
            return true;
        var targets = body[..separator];
        if (!targets.IsEmpty && targets.IndexOfAny((byte)'c', (byte)'s') < 0)
            return true;
        var payload = body[(separator + 1)..];
        if (payload.SequenceEqual("?"u8))
            return true;
        try
        {
            var bytes = Convert.FromBase64String(Encoding.ASCII.GetString(payload));
            var text = new UTF8Encoding(false, true).GetString(bytes);
            ClipboardWriteRequested?.Invoke(text);
        }
        catch (FormatException) { }
        catch (DecoderFallbackException) { }
        return true;
    }

    private bool ReadProgramStatusOsc(int terminatorLength)
    {
        var osc = _oscBuf.AsSpan(0, _oscLen);
        if (osc.StartsWith("7501;"u8))
        {
            if (_oscLen + 2 + terminatorLength > 4096)
                return true;
            var body = osc[5..];
            if (body.SequenceEqual("?"u8))
                WritePty?.Invoke(ProgramStatusReply, ProgramStatusReply.Length);
            else
                _programStatus.ApplyProgramStatus(body);
            return true;
        }
        if (IsOsc133(_oscBuf, _oscLen))
        {
            if (osc.StartsWith("133;"u8))
                _programStatus.ApplyShellMarker(osc[4..]);
            // Preserve the prompt-redraw workaround; consume semantics before stripping the marker.
            return true;
        }
        if (osc.StartsWith("9;4;"u8))
        {
            _programStatus.ApplyProgressReport(osc[4..]);
            return true;
        }
        return false;
    }

    private void ReadWorkingDirectoryOsc()
    {
        if (_oscLen < 3 || _oscBuf[0] != (byte)'7' || _oscBuf[1] != (byte)';')
            return;
        var text = Encoding.UTF8.GetString(_oscBuf, 2, _oscLen - 2);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile)
            return;
        if (_wslDistribution is not null)
        {
            // The Linux hostname may differ from Windows. Never treat it as
            // a network server: the launch distribution owns this path.
            var linuxPath = Uri.UnescapeDataString(uri.AbsolutePath);
            if (!text.Contains('\\') && !text.Contains("%5c", StringComparison.OrdinalIgnoreCase) &&
                linuxPath.StartsWith('/') && linuxPath.IndexOfAny(['\\', ':']) < 0 && !linuxPath.Any(char.IsControl))
                Volatile.Write(ref _workingDirectory, @"\\wsl.localhost\" + _wslDistribution + linuxPath.Replace('/', '\\'));
            return;
        }
        if (!(uri.Host.Length == 0 || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
            return;
        var path = uri.Host.Length == 0 ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = path[1..].Replace('/', '\\');
        if (System.IO.Path.IsPathFullyQualified(path) && !path.Any(char.IsControl))
            Volatile.Write(ref _workingDirectory, path);
    }

    private static bool IsOsc133(byte[] buf, int len)
    {
        // The OSC number is the leading decimal digits; distinguish 133 from
        // 1337 (iTerm2 shell integration) by reading every leading digit.
        var num = 0;
        for (var i = 0; i < len && buf[i] >= (byte)'0' && buf[i] <= (byte)'9'; i++)
        {
            if (num < 10000)
                num = num * 10 + (buf[i] - '0');
        }
        return num == 133;
    }

    private static bool IsQueryByte(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9')
        || (b >= (byte)'A' && b <= (byte)'F')
        || (b >= (byte)'a' && b <= (byte)'f')
        || b == (byte)';';

    /// <summary>
    /// Single vectorized pass deciding whether the chunk needs the byte loop
    /// below: any ESC, any `+q`/`$q` pair, or a trailing `+`/`$` that could
    /// pair across the Feed boundary. Everything else is filter-neutral.
    /// Scanning for `q` (rare) instead of `+`/`$` (common in code and shells)
    /// keeps the scan to one pass with almost no scalar steps.
    /// </summary>
    private static bool NeedsSlowPath(byte[] data, int offset, int count)
    {
        var span = data.AsSpan(offset, count);
        if (span.IsEmpty)
            return false;
        if (span[^1] is (byte)'+' or (byte)'$')
            return true;
        var i = 0;
        while (i < span.Length)
        {
            var j = span[i..].IndexOfAny((byte)0x1B, (byte)'q');
            if (j < 0)
                return false;
            i += j;
            if (span[i] == 0x1B)
                return true;
            // A `q` only matters when directly preceded by `+`/`$`. A pair
            // split across chunks is already covered: the previous chunk
            // ended with `+`/`$`, which forced the slow path above.
            if (i > 0 && span[i - 1] is (byte)'+' or (byte)'$')
                return true;
            i++;
        }
        return false;
    }

    /// <summary>
    /// True when the buffered `+q…` payload is a known XTGETTCAP probe: every
    /// `;`-separated token must be a listed capability. Anything else is
    /// ordinary text (`a+q1B`, base64, …) and must pass through untouched.
    /// </summary>
    private static bool IsStrippedQuery(byte[] buf, int len)
    {
        if (len == 0)
            return false;
        var start = 0;
        for (var i = 0; i <= len; i++)
        {
            if (i == len || buf[i] == (byte)';')
            {
                if (i == start)
                    return false;
                if (!IsKnownCap(buf, start, i - start))
                    return false;
                start = i + 1;
            }
        }
        return true;
    }

    private static bool IsKnownCap(byte[] buf, int offset, int length)
    {
        // Queries are rare (a few per TUI startup), so a short string per
        // token against the case-insensitive set is fine.
        var token = Encoding.ASCII.GetString(buf, offset, length);
        return s_strippedCaps.Contains(token);
    }

    public void Reset()
    {
        lock (_vtLock)
        {
            // Pending one-byte candidates (`+`, `$`, half OSC/DCS payloads)
            // are dropped with the state; Reset has no callers today and the
            // most that can vanish is an unflushed probe fragment.
            _feedState = FeedState.Normal;
            _oscLen = 0;
            _plusLen = 0;
            _programStatus.ResetProgramStatus();
            Native.ghostty_terminal_reset(_terminal);
        }
    }
}
