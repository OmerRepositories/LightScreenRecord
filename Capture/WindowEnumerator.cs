using System.Diagnostics;
using LiteRec.Interop;

namespace LiteRec.Capture;

/// <summary>A top-level window that can be offered to the user as a recording target.</summary>
internal sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public override string ToString() => string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  [{ProcessName}]";
}

/// <summary>Lists the windows a user would recognise as "open applications" (roughly the Alt+Tab list).</summary>
internal static unsafe class WindowEnumerator
{
    public static List<WindowInfo> GetOpenWindows()
    {
        var result = new List<WindowInfo>();
        uint ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsCapturable(hwnd, ownProcessId, out string title, out uint processId))
            {
                result.Add(new WindowInfo(hwnd, title, GetProcessName(processId)));
            }

            return true; // keep enumerating
        }, IntPtr.Zero);

        return result;
    }

    private static bool IsCapturable(IntPtr hwnd, uint ownProcessId, out string title, out uint processId)
    {
        title = string.Empty;
        NativeMethods.GetWindowThreadProcessId(hwnd, out processId);

        // Skip invisible windows, our own windows, owned popups and tool windows.
        if (!NativeMethods.IsWindowVisible(hwnd) || processId == ownProcessId)
        {
            return false;
        }

        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
        {
            return false;
        }

        // "Cloaked" windows are hidden by the shell (other virtual desktops, suspended store apps).
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        int length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return false;
        }

        char* buffer = stackalloc char[Math.Min(length, 255) + 1];
        int copied = NativeMethods.GetWindowTextW(hwnd, buffer, Math.Min(length, 255) + 1);
        if (copied <= 0)
        {
            return false;
        }

        title = new string(buffer, 0, copied);
        return true;
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty; // process exited or access denied; the title alone is enough
        }
    }
}
