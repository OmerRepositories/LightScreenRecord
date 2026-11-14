using System.Runtime.InteropServices;
using LiteRec.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace LiteRec.Capture;

/// <summary>
/// Thin wrapper around Windows Graphics Capture (WGC). WGC hands us the composited window or monitor
/// image as a GPU texture each time its content changes; this class keeps only the newest one and, on
/// request, copies the wanted rectangle of it into an encoder texture. Pixels never leave the GPU.
/// </summary>
internal sealed class ScreenCapturer : IDisposable
{
    private readonly D3DDevice _device;
    private readonly CaptureTarget _target;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ManualResetEventSlim _firstFrame = new(false);

    private Direct3D11CaptureFrame? _latestFrame; // newest undelivered frame, swapped atomically
    private SizeInt32 _poolSize;                  // size of the textures WGC currently renders into
    private IntPtr _blankTexture;                 // black texture used when the window shrinks
    private volatile bool _disposed;

    /// <summary>Size of the recorded picture in pixels (always even, as H.264 requires).</summary>
    public Size FrameSize { get; }

    /// <summary>Raised (on a WGC thread) when the recorded window or monitor goes away.</summary>
    public event Action? TargetClosed;

    public ScreenCapturer(D3DDevice device, CaptureTarget target, bool captureCursor)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows Graphics Capture is not available on this version of Windows.");
        }

        _device = device;
        _target = target;

        Size size;
        if (target.IsWindow)
        {
            if (!NativeMethods.IsWindow(target.Window) || !NativeMethods.GetClientRect(target.Window, out var client))
            {
                throw new InvalidOperationException("The selected window no longer exists.");
            }

            size = new Size(client.Width, client.Height);
            _item = WinRTInterop.CreateItemForWindow(target.Window);
        }
        else
        {
            size = target.MonitorCrop.Size;
            _item = WinRTInterop.CreateItemForMonitor(target.Monitor);
        }

        // H.264 needs even dimensions; drop at most one pixel column/row.
        FrameSize = new Size(size.Width & ~1, size.Height & ~1);
        if (FrameSize.Width < 16 || FrameSize.Height < 16)
        {
            throw new InvalidOperationException("The capture area is too small (the window may be minimized).");
        }

        _poolSize = _item.Size;

        // Two buffers: one we may be holding as "latest", one for WGC to render the next frame into.
        // The free-threaded pool raises FrameArrived on a WGC worker thread, so no UI thread is involved.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _framePool.CreateCaptureSession(_item);

        TryConfigure(() => _session.IsCursorCaptureEnabled = captureCursor);

        // Remove the yellow capture border where the OS allows it (Windows 11).
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) &&
            ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (Exception)
            {
                // Borderless capture can be refused by policy; the border is only cosmetic.
            }
        }

        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
    }

    public void Start() => _session.StartCapture();

    /// <summary>Waits until WGC has delivered its first frame so the recording does not start on black.</summary>
    public bool WaitForFirstFrame(int timeoutMs) => _firstFrame.Wait(timeoutMs);

    /// <summary>
    /// Copies the newest captured frame into <paramref name="destination"/> (a FrameSize texture).
    /// Returns false when the screen content has not changed since the previous call; the caller
    /// then simply repeats its last frame, which costs nothing.
    /// </summary>
    public bool TryCopyLatestFrame(IntPtr destination)
    {
        Direct3D11CaptureFrame? frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame is null)
        {
            return false;
        }

        using (frame)
        {
            SizeInt32 content = frame.ContentSize;
            Rectangle crop = GetSourceRectangle(content);

            // Dispose the surface wrapper explicitly: waiting for the garbage collector would keep
            // the GPU texture (and through it the whole D3D device) alive long after recording ends.
            using IDirect3DSurface surface = frame.Surface;
            IntPtr source = WinRTInterop.GetTexture(surface);
            try
            {
                _device.Enter();
                try
                {
                    bool coversWholeFrame = crop.Width >= FrameSize.Width && crop.Height >= FrameSize.Height;
                    if (!coversWholeFrame)
                    {
                        // The window got smaller than when recording started: clear to black first so
                        // no stale pixels remain around the smaller picture.
                        if (_blankTexture == IntPtr.Zero)
                        {
                            _blankTexture = _device.CreateTexture(FrameSize.Width, FrameSize.Height, zeroFill: true);
                        }

                        _device.CopyResource(destination, _blankTexture);
                    }

                    if (crop.Width > 0 && crop.Height > 0)
                    {
                        var box = new D3DDevice.Box
                        {
                            Left = (uint)crop.Left,
                            Top = (uint)crop.Top,
                            Front = 0,
                            Right = (uint)crop.Right,
                            Bottom = (uint)crop.Bottom,
                            Back = 1,
                        };
                        _device.CopyRegion(destination, source, in box);
                    }
                }
                finally
                {
                    _device.Leave();
                }
            }
            finally
            {
                Marshal.Release(source);
            }

            // If the window was resized, make WGC render into buffers of the new size from now on.
            if ((content.Width != _poolSize.Width || content.Height != _poolSize.Height) && content.Width > 0 && content.Height > 0)
            {
                _poolSize = content;
                _framePool.Recreate(_device.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
        }

        return true;
    }

    /// <summary>
    /// Computes which rectangle of the captured texture should be recorded, clamped so the GPU copy
    /// can never read outside the source texture or write outside the destination texture.
    /// </summary>
    private Rectangle GetSourceRectangle(SizeInt32 content)
    {
        Rectangle wanted;
        if (_target.IsWindow)
        {
            wanted = GetClientAreaInFrame(content);
        }
        else
        {
            wanted = _target.MonitorCrop;
        }

        int maxWidth = Math.Min(content.Width, _poolSize.Width);
        int maxHeight = Math.Min(content.Height, _poolSize.Height);

        int left = Math.Clamp(wanted.Left, 0, maxWidth);
        int top = Math.Clamp(wanted.Top, 0, maxHeight);
        int right = Math.Clamp(Math.Min(wanted.Right, left + FrameSize.Width), left, maxWidth);
        int bottom = Math.Clamp(Math.Min(wanted.Bottom, top + FrameSize.Height), top, maxHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>
    /// A WGC window frame contains the whole window (title bar and borders included). This locates
    /// the client area inside that frame so only the window's actual content is recorded.
    /// </summary>
    private Rectangle GetClientAreaInFrame(SizeInt32 content)
    {
        IntPtr hwnd = _target.Window;
        if (!NativeMethods.GetClientRect(hwnd, out var client) || !NativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            return Rectangle.Empty; // window is being destroyed
        }

        var clientOrigin = new NativeMethods.POINT();
        NativeMethods.ClientToScreen(hwnd, ref clientOrigin);

        // The frame normally matches the visible window bounds reported by DWM (without the invisible
        // resize borders). Some Windows builds capture the raw window rectangle instead; detect by size.
        NativeMethods.RECT frameBounds = windowRect;
        bool frameIsRawWindowRect = content.Width == windowRect.Width && content.Height == windowRect.Height;
        if (!frameIsRawWindowRect &&
            NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT visible, Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            frameBounds = visible;
        }

        return new Rectangle(clientOrigin.X - frameBounds.Left, clientOrigin.Y - frameBounds.Top, client.Width, client.Height);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Keep only the newest frame. Disposing the superseded one returns its buffer to WGC
            // immediately, so a 144 Hz desktop never stalls a 30 fps recording.
            Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is not null)
            {
                Interlocked.Exchange(ref _latestFrame, frame)?.Dispose();
                _firstFrame.Set();
            }
        }
        catch (Exception)
        {
            // The pool can be torn down while a callback is in flight; nothing to do.
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => TargetClosed?.Invoke();

    private static void TryConfigure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Optional session settings are not supported on every Windows build; capture still works.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _framePool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;
        _session.Dispose();
        _framePool.Dispose();
        Interlocked.Exchange(ref _latestFrame, null)?.Dispose();

        if (_blankTexture != IntPtr.Zero)
        {
            Marshal.Release(_blankTexture);
            _blankTexture = IntPtr.Zero;
        }

        _firstFrame.Dispose();
    }
}
