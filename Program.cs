using LiteRec.UI;

namespace LiteRec;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Two instances would fight over the global hotkeys, so allow only one.
        using var mutex = new Mutex(initiallyOwned: true, "LiteRec.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();     // per-monitor DPI awareness, visual styles (see .csproj)
        Application.SetColorMode(SystemColorMode.System); // follow the Windows light/dark setting

        // Record every kind of unexpected error, including those on worker threads, which would
        // otherwise terminate the process silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("Unhandled exception (process terminating)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write("Unobserved task exception", e.Exception); e.SetObserved(); };
        Application.ThreadException += (_, e) =>
        {
            CrashLog.Write("UI thread exception", e.Exception);
            MessageBox.Show($"{e.Exception.Message}\n\n{CrashLog.FilePath}", "LiteRec", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.Run(new MainForm());
    }
}
