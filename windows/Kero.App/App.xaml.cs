using System.Configuration;
using System.Data;
using System.Windows;

namespace Kero.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Required for ported TUI applications (vim, agy, pi, etc.) to
        // output VT sequences properly under ConPTY.
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
    }
}

