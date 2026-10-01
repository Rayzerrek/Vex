using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Vex.App.Model;

/// <summary>Records fatal exceptions locally without suppressing termination or doing startup I/O.</summary>
internal static class AppCrashLog
{
    private static int _recorded;

    internal static void RegisterFatalExceptionHandlers(Application application)
    {
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        RecordFatalException("dispatcher", e.Exception);
        // Do not set Handled: terminal/native state may no longer be safe.
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            RecordFatalException("app-domain", exception);
    }

    private static void RecordFatalException(string source, Exception exception)
    {
        // WPF and AppDomain can report the same fatal failure. Record the
        // first one synchronously; a background write could die with the app.
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
            return;
        try { WriteCrashLog(AppProfile.DirectoryPath, source, exception); }
        catch { /* A broken profile path must not replace the original exception. */ }
    }

    /// <summary>Keeps only the latest bounded crash report; logging failures never escape.</summary>
    internal static void WriteCrashLog(string directory, string source, Exception exception)
    {
        try
        {
            var detail = exception.ToString();
            const int maxDetailCharacters = 32 * 1024;
            if (detail.Length > maxDetailCharacters)
                detail = detail[..maxDetailCharacters] + "\n[truncated]";
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "crash.log"),
                $"Vex fatal exception | {DateTimeOffset.UtcNow:O} | {source} | process {Environment.ProcessId}\n{detail}\n",
                Encoding.UTF8);
        }
        catch { /* Disk/permission failures must not hide the original crash. */ }
    }
}
