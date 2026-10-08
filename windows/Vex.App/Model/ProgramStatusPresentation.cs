using Vex.Libghostty;

namespace Vex.App.Model;

/// <summary>Minimal program status labels shared by pane headers, tabs and the tab overview.</summary>
public static class ProgramStatusPresentation
{
    public static string StatusSymbol(ProgramStatusSummary status) => status.State switch
    {
        ProgramStatusState.Blocked => "?",
        ProgramStatusState.Done => "✓",
        ProgramStatusState.Error => "!",
        _ => "",
    };

    public static string StatusLabel(ProgramStatusSummary status) => status.State switch
    {
        ProgramStatusState.Working => status.Progress is { } progress ? $"Working · {progress}%" : "Working",
        ProgramStatusState.Blocked => status.Kind switch { "permission" => "Needs approval", "auth" => "Needs sign-in", _ => "Needs input" },
        ProgramStatusState.Done => "Finished",
        ProgramStatusState.Error => "Failed",
        _ => "",
    };

    public static string StatusTooltip(ProgramStatusSummary status)
    {
        var label = StatusLabel(status);
        var app = status.Title ?? status.App;
        if (!string.IsNullOrEmpty(app)) label = app + " · " + label;
        return string.IsNullOrEmpty(status.Message) ? label : label + "\n" + status.Message;
    }
}
