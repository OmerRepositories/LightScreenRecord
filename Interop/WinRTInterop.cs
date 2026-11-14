using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LiteRec.Interop;

/// <summary>
/// Bridges between WinRT objects (capture items, Direct3D surfaces, memory buffers) and the raw
/// COM pointers behind them. Everything that depends on the WinRT projection lives in this file.
/// </summary>
internal static unsafe class WinRTInterop
{
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IMemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    /// <summary>Wraps an ID3D11Device as a WinRT IDirect3DDevice.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(IntPtr d3dDevice)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in IID_IDXGIDevice, out IntPtr dxgiDevice));
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable));
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    /// <summary>
    /// Returns the ID3D11Texture2D behind a WinRT surface. The caller owns the returned reference
    /// and must call <see cref="Marshal.Release(IntPtr)"/> on it.
    /// </summary>
    public static IntPtr GetTexture(IDirect3DSurface surface)
    {
        IntPtr unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_IDirect3DDxgiInterfaceAccess, out IntPtr access));
            try
            {
                IntPtr texture;
                fixed (Guid* iid = &IID_ID3D11Texture2D)
                {
                    // IDirect3DDxgiInterfaceAccess::GetInterface is the first method after IUnknown (slot 3).
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)access)[3])(access, iid, &texture);
                    Marshal.ThrowExceptionForHR(hr);
                }

                return texture;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Creates a capture item for a top-level window (works on Windows 10 1903+).</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd) => CreateItem(hwnd, vtableSlot: 3);

    /// <summary>Creates a capture item for a whole monitor.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, vtableSlot: 4);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int vtableSlot)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        IntPtr hstring;
        fixed (char* chars = className)
        {
            Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(chars, (uint)className.Length, out hstring));
        }

        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(hstring, in IID_IGraphicsCaptureItemInterop, out IntPtr interop));
            try
            {
                // IGraphicsCaptureItemInterop: slot 3 = CreateForWindow(HWND), slot 4 = CreateForMonitor(HMONITOR).
                IntPtr item;
                fixed (Guid* iid = &IID_IGraphicsCaptureItem)
                {
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)interop)[vtableSlot])(interop, handle, iid, &item);
                    Marshal.ThrowExceptionForHR(hr);
                }

                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// Gets a direct pointer to the bytes of a locked WinRT memory buffer (used to read audio frames
    /// without copying through managed streams). Valid only while <paramref name="reference"/> is alive.
    /// </summary>
    public static byte* GetBufferPointer(IMemoryBufferReference reference, out uint capacity)
    {
        IntPtr unknown = MarshalInterface<IMemoryBufferReference>.FromManaged(reference);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_IMemoryBufferByteAccess, out IntPtr access));
            try
            {
                byte* data;
                uint size;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)(*(void***)access)[3])(access, &data, &size);
                Marshal.ThrowExceptionForHR(hr);
                capacity = size;
                return data;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
