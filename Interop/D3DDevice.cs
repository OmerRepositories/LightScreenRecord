using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace LiteRec.Interop;

/// <summary>
/// Minimal Direct3D 11 wrapper. The recorder only needs four GPU operations (create texture, copy,
/// copy region, lock), so instead of pulling in a full DirectX binding we call the COM vtables directly.
/// All frame data stays on the GPU: nothing in this class ever reads pixels back to system memory.
/// </summary>
internal sealed unsafe class D3DDevice : IDisposable
{
    // Vtable slots, counted from the interface definitions in d3d11.h / d3d11_4.h.
    private const int Device_CreateTexture2D = 5;
    private const int Context_CopySubresourceRegion = 46;
    private const int Context_CopyResource = 47;
    private const int Multithread_Enter = 3;
    private const int Multithread_Leave = 4;
    private const int Multithread_SetProtected = 5;

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;

    private static readonly Guid IID_ID3D11Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleCount, SampleQuality;
        public uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubresourceData
    {
        public void* SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    private IntPtr _device;       // ID3D11Device*
    private IntPtr _context;      // ID3D11DeviceContext*
    private IntPtr _multithread;  // ID3D11Multithread*

    /// <summary>The raw ID3D11Device pointer (borrowed; shared with the Media Foundation encoder).</summary>
    public IntPtr NativeDevice => _device;

    /// <summary>WinRT view of the device, required by the Windows Graphics Capture frame pool.</summary>
    public IDirect3DDevice WinRTDevice { get; }

    /// <summary>True when the device runs on a real GPU (false = WARP software rasterizer).</summary>
    public bool IsHardware { get; }

    private D3DDevice(IntPtr device, IntPtr context, bool isHardware)
    {
        _device = device;
        _context = context;
        IsHardware = isHardware;

        // The Media Foundation encoder pipeline uses this device from its own threads while our capture
        // thread copies frames, so the immediate context must be switched to thread-safe mode.
        if (Marshal.QueryInterface(_context, in IID_ID3D11Multithread, out _multithread) >= 0 && _multithread != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)VTable(_multithread)[Multithread_SetProtected])(_multithread, 1);
        }

        WinRTDevice = WinRTInterop.CreateDirect3DDevice(_device);
    }

    /// <summary>
    /// Creates a device on the default adapter (the GPU that drives the desktop, which avoids
    /// cross-adapter copies on hybrid laptops). Falls back to the software rasterizer if needed.
    /// </summary>
    public static D3DDevice Create()
    {
        (int driver, uint flags)[] attempts =
        [
            (D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT),
            (D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_BGRA_SUPPORT),
            (D3D_DRIVER_TYPE_WARP, D3D11_CREATE_DEVICE_BGRA_SUPPORT),
        ];

        int hr = 0;
        foreach (var (driver, flags) in attempts)
        {
            hr = NativeMethods.D3D11CreateDevice(IntPtr.Zero, driver, IntPtr.Zero, flags, IntPtr.Zero, 0,
                D3D11_SDK_VERSION, out IntPtr device, out _, out IntPtr context);
            if (hr >= 0)
            {
                return new D3DDevice(device, context, driver == D3D_DRIVER_TYPE_HARDWARE);
            }
        }

        throw new InvalidOperationException($"Could not create a Direct3D 11 device (HRESULT 0x{hr:X8}).");
    }

    /// <summary>Creates a BGRA texture usable as encoder input. Returns an owned ID3D11Texture2D pointer.</summary>
    public IntPtr CreateTexture(int width, int height, bool zeroFill = false)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
        };

        IntPtr texture;
        int hr;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, SubresourceData*, IntPtr*, int>)VTable(_device)[Device_CreateTexture2D];

        if (zeroFill)
        {
            // Texture contents are undefined unless initial data is supplied; upload zeros (opaque black
            // as far as the encoder is concerned, since alpha is ignored).
            nuint size = (nuint)width * (nuint)height * 4;
            void* zeros = NativeMemory.AllocZeroed(size);
            try
            {
                var init = new SubresourceData { SysMem = zeros, SysMemPitch = (uint)width * 4 };
                hr = create(_device, &desc, &init, &texture);
            }
            finally
            {
                NativeMemory.Free(zeros);
            }
        }
        else
        {
            hr = create(_device, &desc, null, &texture);
        }

        Marshal.ThrowExceptionForHR(hr);
        return texture;
    }

    /// <summary>GPU copy of a rectangle of <paramref name="source"/> to the top-left corner of <paramref name="destination"/>.</summary>
    public void CopyRegion(IntPtr destination, IntPtr source, in Box sourceBox)
    {
        fixed (Box* box = &sourceBox)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)
                VTable(_context)[Context_CopySubresourceRegion])(_context, destination, 0, 0, 0, 0, source, 0, box);
        }
    }

    /// <summary>GPU copy of a whole texture (both must have identical size and format).</summary>
    public void CopyResource(IntPtr destination, IntPtr source)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)VTable(_context)[Context_CopyResource])(_context, destination, source);
    }

    /// <summary>Takes the device lock so a sequence of context calls is not interleaved with encoder work.</summary>
    public void Enter()
    {
        if (_multithread != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)VTable(_multithread)[Multithread_Enter])(_multithread);
        }
    }

    public void Leave()
    {
        if (_multithread != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)VTable(_multithread)[Multithread_Leave])(_multithread);
        }
    }

    private static void** VTable(IntPtr comObject) => *(void***)comObject;

    public void Dispose()
    {
        WinRTDevice.Dispose();
        ReleaseAndClear(ref _multithread);
        ReleaseAndClear(ref _context);
        ReleaseAndClear(ref _device);
    }

    private static void ReleaseAndClear(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }
}
