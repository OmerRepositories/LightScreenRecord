using System.Runtime.InteropServices;

namespace LiteRec.Interop;

/// <summary>
/// Minimal bindings for the Media Foundation sink writer (the Windows component that encodes and
/// writes MP4 files), called through raw COM vtables. Slot numbers are counted from mfobjects.h
/// and mfreadwrite.h; each wrapper names the method it calls.
/// </summary>
internal static unsafe class MediaFoundation
{
    private const uint MF_VERSION = 0x00020070;

    // ---- Attribute keys -------------------------------------------------------------------------
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("A634A91C-822B-41B9-A494-4DE4643612B0");
    public static readonly Guid MF_SINK_WRITER_D3D_MANAGER = new("EC822DA2-E1E9-4B29-A0D8-563C719F5269");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08B845D8-2B74-4AFE-9D53-BE16D2D5AE4F");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");

    /// <summary>Encoder setting: distance between key frames, in frames.</summary>
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");

    // ---- Media types ----------------------------------------------------------------------------
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");

    public static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public const uint MFVideoInterlace_Progressive = 2;
    public const uint eAVEncH264VProfile_High = 100;

    // ---- Platform functions ---------------------------------------------------------------------
    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr deviceManager);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGISurfaceBuffer(in Guid interfaceId, IntPtr surface, uint subresourceIndex, bool bottomUpWhenLinear, out IntPtr buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string outputUrl, IntPtr byteStream, IntPtr attributes, out IntPtr sinkWriter);

    public static int Startup() => MFStartup(MF_VERSION, 0);

    private static void** VTable(IntPtr comObject) => *(void***)comObject;

    // ---- IMFAttributes (also the base of IMFMediaType and IMFSample) ------------------------------
    /// <summary>IMFAttributes::SetUINT32 (slot 21).</summary>
    public static void SetUInt32(IntPtr attributes, in Guid key, uint value)
    {
        fixed (Guid* k = &key)
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)VTable(attributes)[21])(attributes, k, value));
        }
    }

    /// <summary>IMFAttributes::SetUINT64 (slot 22). Sizes and ratios are packed as (high &lt;&lt; 32) | low.</summary>
    public static void SetUInt64(IntPtr attributes, in Guid key, uint high, uint low)
    {
        fixed (Guid* k = &key)
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)VTable(attributes)[22])(attributes, k, ((ulong)high << 32) | low));
        }
    }

    /// <summary>IMFAttributes::SetGUID (slot 24).</summary>
    public static void SetGuid(IntPtr attributes, in Guid key, in Guid value)
    {
        fixed (Guid* k = &key, v = &value)
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)VTable(attributes)[24])(attributes, k, v));
        }
    }

    /// <summary>IMFAttributes::SetUnknown (slot 27).</summary>
    public static void SetUnknown(IntPtr attributes, in Guid key, IntPtr value)
    {
        fixed (Guid* k = &key)
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)VTable(attributes)[27])(attributes, k, value));
        }
    }

    // ---- IMFDXGIDeviceManager -------------------------------------------------------------------
    /// <summary>IMFDXGIDeviceManager::ResetDevice (slot 7): tells Media Foundation which GPU device to use.</summary>
    public static void ResetDevice(IntPtr deviceManager, IntPtr d3dDevice, uint resetToken) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)VTable(deviceManager)[7])(deviceManager, d3dDevice, resetToken));

    // ---- IMFSinkWriter --------------------------------------------------------------------------
    /// <summary>IMFSinkWriter::AddStream (slot 3): declares an output stream, returns its index.</summary>
    public static uint AddStream(IntPtr sinkWriter, IntPtr outputType)
    {
        uint index;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)VTable(sinkWriter)[3])(sinkWriter, outputType, &index));
        return index;
    }

    /// <summary>IMFSinkWriter::SetInputMediaType (slot 4): declares what we feed; the writer inserts the needed converters and encoder.</summary>
    public static void SetInputMediaType(IntPtr sinkWriter, uint streamIndex, IntPtr inputType, IntPtr encodingParameters) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)VTable(sinkWriter)[4])(sinkWriter, streamIndex, inputType, encodingParameters));

    /// <summary>IMFSinkWriter::BeginWriting (slot 5).</summary>
    public static void BeginWriting(IntPtr sinkWriter) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(sinkWriter)[5])(sinkWriter));

    /// <summary>IMFSinkWriter::WriteSample (slot 6). Returns the HRESULT; the caller decides how to react.</summary>
    public static int WriteSample(IntPtr sinkWriter, uint streamIndex, IntPtr sample) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)VTable(sinkWriter)[6])(sinkWriter, streamIndex, sample);

    /// <summary>IMFSinkWriter::Finalize (slot 11): flushes the encoders and completes the file. Blocks until done.</summary>
    public static int FinalizeWriter(IntPtr sinkWriter) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(sinkWriter)[11])(sinkWriter);

    // ---- IMFSample ------------------------------------------------------------------------------
    /// <summary>IMFSample::SetSampleTime (slot 36) and ::SetSampleDuration (slot 38), in 100 ns units.</summary>
    public static void SetSampleTiming(IntPtr sample, long time, long duration)
    {
        Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)VTable(sample)[36])(sample, time));
        Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)VTable(sample)[38])(sample, duration));
    }

    /// <summary>IMFSample::AddBuffer (slot 42).</summary>
    public static void AddBuffer(IntPtr sample, IntPtr buffer) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)VTable(sample)[42])(sample, buffer));

    // ---- IMFMediaBuffer -------------------------------------------------------------------------
    /// <summary>IMFMediaBuffer::Lock (slot 3).</summary>
    public static byte* LockBuffer(IntPtr buffer)
    {
        byte* data;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)VTable(buffer)[3])(buffer, &data, null, null));
        return data;
    }

    /// <summary>IMFMediaBuffer::Unlock (slot 4).</summary>
    public static void UnlockBuffer(IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(buffer)[4])(buffer);

    /// <summary>IMFMediaBuffer::SetCurrentLength (slot 6).</summary>
    public static void SetCurrentLength(IntPtr buffer, uint length) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)VTable(buffer)[6])(buffer, length));

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new COMException($"Media Foundation error 0x{hr:X8}.", hr);
        }
    }
}
