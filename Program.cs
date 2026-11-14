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

        // Last line of defence: report unexpected UI-thread errors instead of crashing silently.
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "LiteRec", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm());
    }
}
