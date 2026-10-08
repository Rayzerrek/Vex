using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vex.Libghostty;

/// <summary>Program status is independent of the terminal's primary or alternate screen.</summary>
public enum ProgramStatusState { None, Idle, Working, Done, Blocked, Error }

/// <summary>A bounded OSC 7501 record; each report replaces every field of its record.</summary>
public sealed record ProgramStatusRecord(string Id, ProgramStatusState State, string? Kind = null,
    int? Progress = null, string? App = null, string? Title = null, string? Message = null);

/// <summary>Current program status for presentation; attention remains latched across a UI batch.</summary>
public sealed record ProgramStatusSummary(ProgramStatusState State, string? Kind = null,
    int? Progress = null, string? App = null, string? Title = null, string? Message = null)
{
    public static readonly ProgramStatusSummary Empty = new(ProgramStatusState.None);
    public bool NeedsAttention => State is ProgramStatusState.Blocked or ProgramStatusState.Done or ProgramStatusState.Error;

    /// <summary>Presentation priority shared by records, panes, tabs and projects.</summary>
    public int DisplayPriority => GetDisplayPriority(State);

    /// <summary>Ranks program status for presentation without changing the reported state.</summary>
    public static int GetDisplayPriority(ProgramStatusState state) => state switch
    {
        ProgramStatusState.Blocked => 5, ProgramStatusState.Error => 4, ProgramStatusState.Done => 3,
        ProgramStatusState.Working => 2, ProgramStatusState.Idle => 1, _ => 0,
    };
}

/// <summary>Stores program status and shell integration events under the emulator's existing feed lock.</summary>
internal sealed class ProgramStatusTracker
{
    private const int MaxRecords = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, (ProgramStatusRecord Record, long Updated, bool Unread)> _records = new(StringComparer.Ordinal);
    private long _revision;
    private bool _hasProgramStatus;
    private bool _hasUnreadResults;
    private long _shellStarted;
    private bool _shellRunning;
    private ProgramStatusSummary _shellStatus = ProgramStatusSummary.Empty;

    internal ProgramStatusSummary Summary { get; private set; } = ProgramStatusSummary.Empty;
    internal event Action<bool>? Changed;

    internal void ApplyProgramStatus(ReadOnlySpan<byte> body)
    {
        if (!TryParseProgramStatus(body, out var record, out var clear))
            return;
        _hasProgramStatus = true;
        var attention = !clear && record!.State is ProgramStatusState.Blocked or ProgramStatusState.Done or ProgramStatusState.Error
            && (!_records.TryGetValue(record.Id, out var previous) || previous.Record.State != record.State || previous.Record.Kind != record.Kind || !previous.Unread);
        if (clear)
        {
            var prefix = record!.Id.Length == 0 ? "" : record.Id + "/";
            foreach (var id in _records.Keys.ToArray())
                if (id == record.Id || id.StartsWith(prefix, StringComparison.Ordinal))
                    _records.Remove(id);
        }
        else
        {
            if (!_records.ContainsKey(record!.Id) && _records.Count == MaxRecords)
            {
                var oldest = _records.MinBy(pair => pair.Value.Updated).Key;
                _records.Remove(oldest);
            }
            _records[record.Id] = (record, ++_revision, true);
            if (record.State is ProgramStatusState.Done or ProgramStatusState.Error) _hasUnreadResults = true;
        }
        RefreshSummary(attention);
    }

    internal void ApplyShellMarker(ReadOnlySpan<byte> body)
    {
        if (body.SequenceEqual("A"u8) || body.StartsWith("A;"u8))
        {
            RemoveRunningRecords();
            if (_shellRunning)
                CompleteShellCommand(0);
            if (_shellStatus.State is not (ProgramStatusState.Done or ProgramStatusState.Error))
                _shellStatus = new(ProgramStatusState.Idle);
            RefreshSummary(false);
        }
        else if (body.SequenceEqual("C"u8) || body.StartsWith("C;"u8))
        {
            _shellRunning = true;
            _shellStarted = Stopwatch.GetTimestamp();
            _shellStatus = new(ProgramStatusState.Working);
            RefreshSummary(false);
        }
        else if (body.SequenceEqual("D"u8) || body.StartsWith("D;"u8))
        {
            var code = 0;
            if (body.Length > 2)
                int.TryParse(Encoding.ASCII.GetString(body[2..]), out code);
            CompleteShellCommand(code);
            RefreshSummary(_shellStatus.NeedsAttention);
        }
    }

    internal void ApplyProgressReport(ReadOnlySpan<byte> body)
    {
        if (_hasProgramStatus)
            return;
        var separator = body.IndexOf((byte)';');
        var state = separator < 0 ? body : body[..separator];
        int? progress = null;
        if (separator >= 0 && TryParsePercentage(body[(separator + 1)..], out var value))
            progress = value;
        _shellStatus = state switch
        {
            var s when s.SequenceEqual("0"u8) => ProgramStatusSummary.Empty,
            var s when s.SequenceEqual("1"u8) => new(ProgramStatusState.Working, Progress: progress),
            var s when s.SequenceEqual("2"u8) => new(ProgramStatusState.Error, Progress: progress),
            var s when s.SequenceEqual("3"u8) => new(ProgramStatusState.Working),
            var s when s.SequenceEqual("4"u8) => new(ProgramStatusState.Blocked, Progress: progress),
            _ => _shellStatus,
        };
        RefreshSummary(_shellStatus.NeedsAttention);
    }

    internal void AcknowledgeProgramStatus()
    {
        if (!_hasUnreadResults && _shellStatus.State is not (ProgramStatusState.Done or ProgramStatusState.Error))
            return;
        _hasUnreadResults = false;
        foreach (var id in _records.Keys.ToArray())
        {
            var entry = _records[id];
            if (entry.Record.State is ProgramStatusState.Done or ProgramStatusState.Error)
                _records[id] = (entry.Record, entry.Updated, false);
        }
        if (_shellStatus.State is ProgramStatusState.Done or ProgramStatusState.Error)
            _shellStatus = ProgramStatusSummary.Empty;
        RefreshSummary(false);
    }

    internal void OnProgramExited()
    {
        RemoveRunningRecords();
        _shellRunning = false;
        if (_shellStatus.State is not (ProgramStatusState.Done or ProgramStatusState.Error))
            _shellStatus = ProgramStatusSummary.Empty;
        RefreshSummary(false);
    }

    internal void ResetProgramStatus()
    {
        _records.Clear();
        _hasProgramStatus = false;
        _hasUnreadResults = false;
        _shellRunning = false;
        _shellStatus = ProgramStatusSummary.Empty;
        RefreshSummary(false);
    }

    private void RemoveRunningRecords()
    {
        foreach (var id in _records.Keys.ToArray())
            if (_records[id].Record.State is ProgramStatusState.Working or ProgramStatusState.Blocked or ProgramStatusState.Idle)
                _records.Remove(id);
    }

    private void CompleteShellCommand(int exitCode)
    {
        if (!_shellRunning)
            return;
        _shellRunning = false;
        // Successful short commands should not turn routine shell use into a stream of alerts.
        _shellStatus = exitCode != 0 ? new(ProgramStatusState.Error, Message: $"Exited with code {exitCode}")
            : Stopwatch.GetElapsedTime(_shellStarted).TotalSeconds >= 2 ? new(ProgramStatusState.Done)
            : new(ProgramStatusState.Idle);
    }

    private void RefreshSummary(bool attention)
    {
        ProgramStatusRecord? selected = null;
        var priority = 0;
        var updated = 0L;
        foreach (var entry in _records.Values)
        {
            if (!entry.Unread && entry.Record.State is ProgramStatusState.Done or ProgramStatusState.Error)
                continue;
            var candidate = ProgramStatusSummary.GetDisplayPriority(entry.Record.State);
            if (candidate > priority || candidate == priority && entry.Updated > updated)
            {
                selected = entry.Record;
                priority = candidate;
                updated = entry.Updated;
            }
        }
        var summary = selected is null ? _shellStatus : new ProgramStatusSummary(selected.State, selected.Kind,
            selected.Progress, ResolveProgramApp(selected), selected.Title, selected.Message);
        if (summary == Summary && !attention)
            return;
        Summary = summary;
        Changed?.Invoke(attention);
    }

    private string? ResolveProgramApp(ProgramStatusRecord record)
    {
        if (record.App is not null)
            return record.App;
        var id = record.Id;
        while (id.Length > 0)
        {
            var separator = id.LastIndexOf('/');
            id = separator < 0 ? "" : id[..separator];
            if (_records.TryGetValue(id, out var ancestor) && ancestor.Record.App is not null)
                return ancestor.Record.App;
        }
        return null;
    }

    private static bool TryParseProgramStatus(ReadOnlySpan<byte> body, out ProgramStatusRecord? record, out bool clear)
    {
        record = null;
        clear = false;
        var state = ProgramStatusState.None;
        ReadOnlySpan<byte> idValue = default;
        var hasId = false;
        string? kind = null, app = null, title = null, message = null;
        int? progress = null;
        foreach (var range in body.Split((byte)':'))
        {
            var pair = TrimAsciiWhitespace(body[range]);
            var separator = pair.IndexOf((byte)'=');
            if (separator <= 0)
                continue;
            var key = TrimAsciiWhitespace(pair[..separator]);
            var value = TrimAsciiWhitespace(pair[(separator + 1)..]);
            if (key.Length > 16 || !IsStatusFieldWithinLimits(key, value))
                return false;
            if (key.Length == 0 || !IsLowercaseKey(key) || !IsProtocolValue(value))
                continue;
            if (key.SequenceEqual("state"u8))
            {
                clear = value.SequenceEqual("clear"u8);
                state = value switch
                {
                    var s when s.SequenceEqual("idle"u8) => ProgramStatusState.Idle,
                    var s when s.SequenceEqual("working"u8) => ProgramStatusState.Working,
                    var s when s.SequenceEqual("done"u8) => ProgramStatusState.Done,
                    var s when s.SequenceEqual("blocked"u8) => ProgramStatusState.Blocked,
                    var s when s.SequenceEqual("error"u8) => ProgramStatusState.Error,
                    _ => ProgramStatusState.None,
                };
            }
            else if (key.SequenceEqual("id"u8))
            {
                idValue = value;
                hasId = true;
            }
            else if (key.SequenceEqual("app"u8))
            {
                app = IsIdSegment(value) ? Encoding.ASCII.GetString(value) : null;
            }
            else if (key.SequenceEqual("kind"u8))
                kind = value.SequenceEqual("permission"u8) ? "permission" : value.SequenceEqual("question"u8) ? "question"
                    : value.SequenceEqual("auth"u8) ? "auth" : null;
            else if (key.SequenceEqual("progress"u8))
                progress = TryParsePercentage(value, out var percentage) ? percentage : null;
            else if (key.SequenceEqual("msg"u8))
            {
                if (!TryDecodeStatusText(value, 2732, 2048, out message)) return false;
            }
            else if (key.SequenceEqual("title"u8))
            {
                if (!TryDecodeStatusText(value, 256, 192, out title)) return false;
            }
        }
        if (state == ProgramStatusState.None && !clear || hasId && !IsRecordId(idValue))
            return false;
        var id = hasId ? Encoding.ASCII.GetString(idValue) : "";
        record = new(id, state, state == ProgramStatusState.Blocked ? kind : null,
            state is ProgramStatusState.Working or ProgramStatusState.Blocked ? progress : null, app, title, message);
        return true;
    }

    private static bool TryDecodeStatusText(ReadOnlySpan<byte> encoded, int maxEncoded, int maxDecoded, out string? text)
    {
        text = null;
        if (encoded.Length > maxEncoded || encoded.Length % 4 == 1)
            return false;
        var length = (encoded.Length + 3) / 4 * 4;
        Span<char> characters = stackalloc char[length];
        for (var i = 0; i < encoded.Length; i++) characters[i] = (char)encoded[i];
        characters[encoded.Length..].Fill('=');
        Span<byte> decoded = stackalloc byte[maxDecoded];
        if (!Convert.TryFromBase64Chars(characters, decoded, out var written))
            return false;
        try { text = StrictUtf8.GetString(decoded[..written]); }
        catch (DecoderFallbackException) { return false; }
        if (text.Any(char.IsControl))
            return false;
        // Labels outside the grid must not let bidi overrides impersonate another pane.
        StringBuilder? visibleText = null;
        var offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
                visibleText ??= new StringBuilder(text.Length).Append(text, 0, offset);
            else
                visibleText?.Append(text, offset, rune.Utf16SequenceLength);
            offset += rune.Utf16SequenceLength;
        }
        if (visibleText is not null) text = visibleText.ToString();
        return true;
    }

    private static bool IsStatusFieldWithinLimits(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        // Limits apply to every occurrence, even if a malformed pair is skipped
        // or a later duplicate would replace it. Grammar uses the final id only.
        if (key.SequenceEqual("app"u8)) return value.Length <= 32;
        if (key.SequenceEqual("msg"u8)) return value.Length <= 2732;
        if (key.SequenceEqual("title"u8)) return value.Length <= 256;
        if (!key.SequenceEqual("id"u8)) return true;
        if (value.Length > 128) return false;
        var depth = 0;
        foreach (var range in value.Split((byte)'/'))
            if (++depth > 8 || value[range].Length > 32) return false;
        return true;
    }

    private static bool TryParsePercentage(ReadOnlySpan<byte> value, out int percentage)
    {
        percentage = 0;
        if (value.Length is 0 or > 3) return false;
        foreach (var digit in value)
        {
            if (digit < '0' || digit > '9') return false;
            percentage = percentage * 10 + digit - '0';
        }
        return percentage <= 100;
    }

    private static bool IsRecordId(ReadOnlySpan<byte> id)
    {
        var depth = 0;
        foreach (var range in id.Split((byte)'/'))
            if (++depth > 8 || !IsIdSegment(id[range])) return false;
        return true;
    }

    private static bool IsIdSegment(ReadOnlySpan<byte> value)
    {
        if (value.Length is 0 or > 32) return false;
        foreach (var b in value)
            if (!IsAsciiAlphanumeric(b) && b is not ((byte)'_' or (byte)'.' or (byte)'+' or (byte)'-')) return false;
        return true;
    }

    private static bool IsLowercaseKey(ReadOnlySpan<byte> key)
    {
        foreach (var b in key) if (b < 'a' || b > 'z') return false;
        return true;
    }

    private static bool IsProtocolValue(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
            if (!IsAsciiAlphanumeric(b) && b is not ((byte)'_' or (byte)'.' or (byte)',' or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'-')) return false;
        return true;
    }

    private static bool IsAsciiAlphanumeric(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9';
    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        while (value.Length > 0 && value[0] is >= 9 and <= 13 or 32) value = value[1..];
        while (value.Length > 0 && value[^1] is >= 9 and <= 13 or 32) value = value[..^1];
        return value;
    }
}
