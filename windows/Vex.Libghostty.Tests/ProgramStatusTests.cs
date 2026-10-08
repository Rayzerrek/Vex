using System.Text;
using Xunit;

namespace Vex.Libghostty.Tests;

public sealed class ProgramStatusTests
{
    [Theory]
    [InlineData("\v")]
    [InlineData("\f")]
    public void AsciiWhitespaceAroundPairs_IsRemoved(string whitespace)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, whitespace + "state" + whitespace + "=" + whitespace + "working" + whitespace);
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
    }

    [Fact]
    public void DuplicateId_LastValueDeterminesRecordAddress()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=blocked:id=bad//id:id=correct");
        Assert.Equal(ProgramStatusState.Blocked, terminal.ProgramStatus.State);
        Report(terminal, "state=clear:id=correct");
        Assert.Equal(ProgramStatusState.None, terminal.ProgramStatus.State);
        Report(terminal, "state=working:id=correct:id=bad//id");
        Assert.Equal(ProgramStatusState.None, terminal.ProgramStatus.State);
    }

    [Theory]
    [InlineData("app", 33)]
    [InlineData("id", 129)]
    [InlineData("title", 257)]
    [InlineData("msg", 2733)]
    public void OversizedMalformedField_DiscardsEntireReport(string key, int length)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=working:progress=12");
        Report(terminal, "state=error:" + key + "=" + new string('A', length - 1) + "!");
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        Assert.Equal(12, terminal.ProgramStatus.Progress);
    }

    [Theory]
    [InlineData("id=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:id=valid")]
    [InlineData("id=a/b/c/d/e/f/g/h/i:id=valid")]
    [InlineData("app=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:app=valid")]
    public void OversizedEarlierDuplicate_DiscardsEntireReport(string fields)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=working:progress=12");
        Report(terminal, "state=error:" + fields);
        Assert.Equal(12, terminal.ProgramStatus.Progress);
    }

    [Theory]
    [InlineData("\x1b\x07")]
    [InlineData("\x1b\x1b\\")]
    public void MalformedEscapeBeforeTerminator_PreservesFollowingText(string terminator)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("\x1b]7501;state=working:future=bad" + terminator + "ok");
        terminal.UpdateFrame();
        Assert.Equal(2, terminal.Cursor.X);
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
    }

    [Fact]
    public void StatusText_RemovesBmpAndSupplementaryInvisibleFormatting()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var text = "safe\u202E\U000E0001 label";
        Report(terminal, "state=blocked:msg=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("safe label", terminal.ProgramStatus.Message);
    }

    [Fact]
    public void SoftReset_PreservesStatusAndMissingProgressIsIndeterminate()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=working:progress=30");
        Report(terminal, "state=working");
        terminal.Feed("\x1b[!p");
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        Assert.Null(terminal.ProgramStatus.Progress);
    }

    [Theory]
    [InlineData("\x07")]
    [InlineData("\x1b\\")]
    public void FragmentedReportAndCapabilityQuery_PreserveTextAndResponseOrder(string terminator)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var responses = new List<string>();
        terminal.WritePty += (bytes, count) => responses.Add(Encoding.UTF8.GetString(bytes, 0, count));
        var stream = "before\x1b]7501;state=working:progress=42" + terminator + "after\x1b]7501;?" + terminator + "\x1b[c";
        foreach (var b in Encoding.UTF8.GetBytes(stream)) terminal.Feed(new[] { b }, 0, 1);
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        Assert.Equal(42, terminal.ProgramStatus.Progress);
        Assert.Equal("\x1b]7501;?\x1b\\", responses[0]);
        Assert.Contains(responses, response => response.StartsWith("\x1b[?"));
        terminal.UpdateFrame();
        Assert.Equal(11, terminal.Cursor.X);
    }

    [Fact]
    public void ChildRecords_InheritAppAndClearOnlyTheirOwnSubtree()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=working:app=build");
        Report(terminal, "state=blocked:id=test/slow:kind=permission");
        Report(terminal, "state=working:id=testing");
        Assert.Equal("build", terminal.ProgramStatus.App);
        Assert.Equal("permission", terminal.ProgramStatus.Kind);
        Report(terminal, "state=clear:id=test");
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        Report(terminal, "state=clear");
        Assert.Equal(ProgramStatusState.None, terminal.ProgramStatus.State);
    }

    [Fact]
    public void ReportsReplaceFields_UnknownAndMalformedPairsDoNotHideValidState()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=blocked:app=test:kind=question:progress=50:msg=SGVsbG8=");
        Report(terminal, "state=done:state=working:malformed:future=value:progress=101");
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        Assert.Null(terminal.ProgramStatus.App);
        Assert.Null(terminal.ProgramStatus.Kind);
        Assert.Null(terminal.ProgramStatus.Progress);
        Assert.Null(terminal.ProgramStatus.Message);
    }

    [Theory]
    [InlineData("state=unknown")]
    [InlineData("state=error:id=bad//id")]
    [InlineData("state=error:msg=A")]
    [InlineData("state=error:msg=Cg==")]
    [InlineData("state=error:msg=/w==")]
    [InlineData("state=error:abcdefghijklmnopq=x")]
    [InlineData("state=error:id=a/b/c/d/e/f/g/h/i")]
    public void InvalidReport_IsDiscardedAtomically(string body)
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=working:progress=25");
        Report(terminal, body);
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        Assert.Equal(25, terminal.ProgramStatus.Progress);
    }

    [Fact]
    public void LongLegalMessage_IsAcceptedAndOversizedReportDoesNotLeakIntoGrid()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var message = new string('x', 2048);
        Report(terminal, "state=done:msg=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(message)));
        Assert.Equal(message, terminal.ProgramStatus.Message);
        Report(terminal, "state=error:msg=" + new string('A', 5000));
        terminal.Feed("ok");
        Assert.Equal(ProgramStatusState.Done, terminal.ProgramStatus.State);
        terminal.UpdateFrame();
        Assert.Equal(2, terminal.Cursor.X);
    }

    [Fact]
    public void OversizedReport_RepeatedEscapeBeforeTerminatorDoesNotConsumeFollowingText()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("\x1b]7501;state=working:msg=" + new string('A', 5000) + "\x1b\x1b\\ok");
        terminal.UpdateFrame();
        Assert.Equal(ProgramStatusState.None, terminal.ProgramStatus.State);
        Assert.Equal(2, terminal.Cursor.X);
    }

    [Fact]
    public void PromptExitAndScreenChanges_RespectPersistentResults()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=done:id=finished");
        Report(terminal, "state=blocked:id=pending");
        terminal.Feed("\x1b[?1049h\x1b[?1049l");
        Assert.Equal(ProgramStatusState.Blocked, terminal.ProgramStatus.State);
        terminal.Feed("\x1b]133;A\x07");
        Assert.Equal(ProgramStatusState.Done, terminal.ProgramStatus.State);
        terminal.NotifyProgramExited();
        Assert.Equal(ProgramStatusState.Done, terminal.ProgramStatus.State);
        terminal.AcknowledgeProgramStatus();
        Assert.False(terminal.ProgramStatus.NeedsAttention);
        Report(terminal, "state=blocked");
        terminal.AcknowledgeProgramStatus();
        Assert.Equal(ProgramStatusState.Blocked, terminal.ProgramStatus.State);
        terminal.Feed("\u001bc");
        Assert.Equal(ProgramStatusState.None, terminal.ProgramStatus.State);
    }

    [Fact]
    public void LegacyProgress_CannotOverwriteExplicitStatusUntilFullReset()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("\x1b]9;4;1;30\x07");
        Assert.Equal(30, terminal.ProgramStatus.Progress);
        Report(terminal, "state=blocked:kind=auth");
        terminal.Feed("\x1b]9;4;1;90\x07");
        Assert.Equal("auth", terminal.ProgramStatus.Kind);
        terminal.Reset();
        terminal.Feed("\x1b]9;4;1;90\x07");
        Assert.Equal(90, terminal.ProgramStatus.Progress);
    }

    [Fact]
    public void RecordCap_EvictsLeastRecentlyUpdatedRecord()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        Report(terminal, "state=blocked:id=oldest");
        for (var i = 0; i < 256; i++) Report(terminal, $"state=working:id=task{i}");
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
    }

    [Fact]
    public void ShellIntegration_ReportsFailureWithoutMovingPrompt()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.Feed("prompt> \x1b]133;A\x07\x1b]133;C\x07");
        Assert.Equal(ProgramStatusState.Working, terminal.ProgramStatus.State);
        terminal.Feed("\x1b]133;D;2\x07\x1b]133;A\x07");
        Assert.Equal(ProgramStatusState.Error, terminal.ProgramStatus.State);
        terminal.UpdateFrame();
        Assert.Equal(8, terminal.Cursor.X);
    }

    private static void Report(GhosttyTerminal terminal, string body) => terminal.Feed("\x1b]7501;" + body + "\x1b\\");
}
