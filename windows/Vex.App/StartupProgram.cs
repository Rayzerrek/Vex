using System.Runtime.CompilerServices;
using Vex.App.Model;

namespace Vex.App;

internal static class StartupProgram
{
    [STAThread]
    private static void Main()
    {
        StartupMark.Note("entrypoint begin");
        RunApplication();
    }

    // Keep WPF type loading out of the entrypoint's diagnostic timing mark.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
