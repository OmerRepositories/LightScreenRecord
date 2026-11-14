using System.Runtime.InteropServices;

namespace LiteRec.Interop;

/// <summary>
/// Minimal bindings for the Windows Core Audio APIs (MMDevice + WASAPI), called through raw COM
/// vtables. Covers exactly what a recorder needs: list endpoints, open one, and pull captured audio.
/// </summary>
internal static unsafe class CoreAudio
{
    public enum DataFlow
    {
        Render = 0,  // playback devices (recorded through loopback)
        Capture = 1, // microphones and other inputs
    }

    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_NOTFOUND = unchecked((int)0x80070490);

    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private const uint CLSCTX_ALL = 0x17;
    private const uint DEVICE_STATE_ACTIVE = 0x1;
    private const ushort VT_LPWSTR = 31;
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    private static readonly PropertyKey PKEY_Device_FriendlyName = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14,
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(void* propVariant);

    private static void** VTable(IntPtr comObject) => *(void***)comObject;

    // ---------------------------------------------------------------------------------------------
    // Device enumeration
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns (endpoint id, friendly name) for every active device of the given kind.</summary>
    public static List<(string Id, string Name)> EnumerateDevices(DataFlow flow)
    {
        var result = new List<(string, string)>();
        IntPtr enumerator = CreateEnumerator();
        try
        {
            // IMMDeviceEnumerator::EnumAudioEndpoints (slot 3)
            IntPtr collection;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)VTable(enumerator)[3])(enumerator, (int)flow, DEVICE_STATE_ACTIVE, &collection);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                // IMMDeviceCollection::GetCount (slot 3), ::Item (slot 4)
                uint count;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VTable(collection)[3])(collection, &count));
                for (uint i = 0; i < count; i++)
                {
                    IntPtr device;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(collection)[4])(collection, i, &device) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        string? id = GetDeviceId(device);
                        if (id is not null)
                        {
                            result.Add((id, GetFriendlyName(device) ?? id));
                        }
                    }
                    finally
                    {
                        Marshal.Release(device);
                    }
                }
            }
            finally
            {
                Marshal.Release(collection);
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }

        return result;
    }

    private static IntPtr CreateEnumerator()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(in CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, in IID_IMMDeviceEnumerator, out IntPtr enumerator));
        return enumerator;
    }

    private static string? GetDeviceId(IntPtr device)
    {
        // IMMDevice::GetId (slot 5) returns a CoTaskMem string.
        IntPtr text;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTable(device)[5])(device, &text) < 0 || text == IntPtr.Zero)
        {
            return null;
        }

        string id = Marshal.PtrToStringUni(text)!;
        Marshal.FreeCoTaskMem(text);
        return id;
    }

    private static string? GetFriendlyName(IntPtr device)
    {
        // IMMDevice::OpenPropertyStore (slot 4), IPropertyStore::GetValue (slot 5)
        IntPtr store;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(device)[4])(device, 0 /* STGM_READ */, &store) < 0)
        {
            return null;
        }

        try
        {
            byte* variant = stackalloc byte[24]; // PROPVARIANT: type tag at offset 0, value at offset 8
            new Span<byte>(variant, 24).Clear();
            PropertyKey key = PKEY_Device_FriendlyName;
            if (((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, void*, int>)VTable(store)[5])(store, &key, variant) < 0)
            {
                return null;
            }

            string? name = *(ushort*)variant == VT_LPWSTR ? Marshal.PtrToStringUni(*(IntPtr*)(variant + 8)) : null;
            PropVariantClear(variant);
            return name;
        }
        finally
        {
            Marshal.Release(store);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Opening a capture stream
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a shared-mode capture stream that delivers 32-bit float PCM in the requested layout,
    /// whatever the device's native format is (Windows inserts the converter). For a playback
    /// device pass <paramref name="loopback"/> = true to record what is being played on it.
    /// Returns owned IAudioClient and IAudioCaptureClient pointers.
    /// </summary>
    public static void OpenCaptureStream(string deviceId, DataFlow flow, bool loopback, int sampleRate, int channels,
        out IntPtr audioClient, out IntPtr captureClient)
    {
        audioClient = IntPtr.Zero;
        captureClient = IntPtr.Zero;

        IntPtr enumerator = CreateEnumerator();
        IntPtr device = IntPtr.Zero;
        try
        {
            int hr;
            if (deviceId.Length == 0)
            {
                // IMMDeviceEnumerator::GetDefaultAudioEndpoint (slot 4), role 0 = eConsole
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)VTable(enumerator)[4])(enumerator, (int)flow, 0, &device);
            }
            else
            {
                // IMMDeviceEnumerator::GetDevice (slot 5)
                fixed (char* id = deviceId)
                {
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)VTable(enumerator)[5])(enumerator, id, &device);
                }
            }

            Marshal.ThrowExceptionForHR(hr);

            // IMMDevice::Activate (slot 3)
            IntPtr client;
            fixed (Guid* iid = &IID_IAudioClient)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, void*, IntPtr*, int>)VTable(device)[3])(device, iid, CLSCTX_ALL, null, &client);
            }

            Marshal.ThrowExceptionForHR(hr);
            audioClient = client;

            var format = new WaveFormatEx
            {
                FormatTag = WAVE_FORMAT_IEEE_FLOAT,
                Channels = (ushort)channels,
                SamplesPerSec = (uint)sampleRate,
                BitsPerSample = 32,
                BlockAlign = (ushort)(channels * sizeof(float)),
                AvgBytesPerSec = (uint)(sampleRate * channels * sizeof(float)),
            };

            uint flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (loopback)
            {
                flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
            }

            // IAudioClient::Initialize (slot 3): shared mode, 200 ms device buffer so a briefly
            // stalled reader loses nothing.
            const long bufferDuration = 200 * TimeSpan.TicksPerMillisecond;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WaveFormatEx*, Guid*, int>)VTable(client)[3])(
                client, 0, flags, bufferDuration, 0, &format, null);
            Marshal.ThrowExceptionForHR(hr);

            // IAudioClient::GetService (slot 14)
            IntPtr capture;
            fixed (Guid* iid = &IID_IAudioCaptureClient)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)VTable(client)[14])(client, iid, &capture);
            }

            Marshal.ThrowExceptionForHR(hr);
            captureClient = capture;
        }
        catch
        {
            if (audioClient != IntPtr.Zero)
            {
                Marshal.Release(audioClient);
                audioClient = IntPtr.Zero;
            }

            throw;
        }
        finally
        {
            if (device != IntPtr.Zero)
            {
                Marshal.Release(device);
            }

            Marshal.Release(enumerator);
        }
    }

    /// <summary>IAudioClient::Start (slot 10).</summary>
    public static int Start(IntPtr audioClient) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(audioClient)[10])(audioClient);

    /// <summary>IAudioClient::Stop (slot 11).</summary>
    public static int Stop(IntPtr audioClient) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(audioClient)[11])(audioClient);

    /// <summary>IAudioCaptureClient::GetNextPacketSize (slot 5): frames waiting in the next packet, 0 if none.</summary>
    public static int GetNextPacketSize(IntPtr captureClient, out uint frames)
    {
        uint value;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VTable(captureClient)[5])(captureClient, &value);
        frames = value;
        return hr;
    }

    /// <summary>IAudioCaptureClient::GetBuffer (slot 3). Must be paired with <see cref="ReleaseBuffer"/>.</summary>
    public static int GetBuffer(IntPtr captureClient, out float* data, out uint frames, out uint flags)
    {
        byte* buffer;
        uint frameCount, bufferFlags;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)VTable(captureClient)[3])(
            captureClient, &buffer, &frameCount, &bufferFlags, null, null);
        data = (float*)buffer;
        frames = frameCount;
        flags = bufferFlags;
        return hr;
    }

    /// <summary>IAudioCaptureClient::ReleaseBuffer (slot 4).</summary>
    public static int ReleaseBuffer(IntPtr captureClient, uint frames) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)VTable(captureClient)[4])(captureClient, frames);
}
