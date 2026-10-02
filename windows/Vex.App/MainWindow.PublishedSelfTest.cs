#if DEBUG || VEX_SELFTEST
using System.Windows.Threading;

namespace Vex.App;

public sealed partial class MainWindow
{
    // Exercise reflection-driven BAML and image/font loading in the published
    // bundle too: tests against loose framework DLLs cannot catch pruning errors.
    internal bool SelfTestStartupOverlays()
    {
        SettingsOverlay.Show();
        try
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (!SettingsOverlay.IsLoaded || !SettingsOverlay.IsVisible)
                return false;
        }
        finally { SettingsOverlay.Hide(); }

        ShowCommandPalette();
        try
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (!PaletteOverlay.IsLoaded || !PaletteOverlay.IsVisible)
                return false;
        }
        finally { PaletteOverlay.Hide(); }

        ThemeSwitcher.Show();
        try
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            return ThemeSwitcher.IsLoaded && ThemeSwitcher.IsVisible;
        }
        finally { ThemeSwitcher.Hide(); }
    }
}
#endif
