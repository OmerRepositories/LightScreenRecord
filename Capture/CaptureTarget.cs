using LiteRec.Interop;

namespace LiteRec.Capture;

/// <summary>
/// Describes what to record. All coordinates are physical screen pixels (the process is per-monitor DPI aware).
/// </summary>
internal sealed class CaptureTarget
{
    /// <summary>Window handle for window capture; zero for monitor/region capture.</summary>
    public IntPtr Window { get; private init; }

    /// <summary>Monitor handle for monitor/region capture; zero for window capture.</summary>
    public IntPtr Monitor { get; private init; }

    /// <summary>Recorded rectangle relative to the monitor's top-left corner (monitor/region capture only).</summary>
    public Rectangle MonitorCrop { get; private init; }

    /// <summary>Recorded rectangle in screen coordinates (monitor/region capture only), used to draw the outline.</summary>
    public Rectangle ScreenBounds { get; private init; }

    public bool IsWindow => Window != IntPtr.Zero;

    /// <summary>Records exactly the client area of a window and follows the window wherever it moves.</summary>
    public static CaptureTarget ForWindow(IntPtr hwnd) => new() { Window = hwnd };

    /// <summary>Records an entire monitor.</summary>
    public static CaptureTarget ForScreen(Screen screen) => ForRegion(screen.Bounds);

    /// <summary>
    /// Records a fixed rectangle of the desktop. A region must live on a single monitor, so it is
    /// clipped to the monitor that contains most of it.
    /// </summary>
    public static CaptureTarget ForRegion(Rectangle screenRect)
    {
        Screen screen = Screen.FromRectangle(screenRect);
        Rectangle clipped = Rectangle.Intersect(screenRect, screen.Bounds);
        if (clipped.Width < 2 || clipped.Height < 2)
        {
            throw new ArgumentException("The selected region is empty.");
        }

        var rect = new NativeMethods.RECT
        {
            Left = screen.Bounds.Left,
            Top = screen.Bounds.Top,
            Right = screen.Bounds.Right,
            Bottom = screen.Bounds.Bottom,
        };

        return new CaptureTarget
        {
            Monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST),
            MonitorCrop = new Rectangle(clipped.X - screen.Bounds.X, clipped.Y - screen.Bounds.Y, clipped.Width, clipped.Height),
            ScreenBounds = clipped,
        };
    }
}
