using System.Runtime.InteropServices;
using LiteRec.Audio;
using LiteRec.Interop;
using MF = LiteRec.Interop.MediaFoundation;

namespace LiteRec.Encoding;

/// <summary>
/// Writes an MP4 file (H.264 video + AAC audio) with the Media Foundation sink writer.
///
/// The sink writer shares our Direct3D device, so each frame goes texture -> GPU colour
/// conversion/scaling -> hardware encoder (Intel Quick Sync, NVIDIA NVENC or AMD AMF, whichever the
/// GPU provides) without the pixels ever being copied into system memory. That is what keeps CPU
/// and RAM use low. If no hardware encoder exists, Windows' software encoder is used automatically.
/// Encoding and muxing run on the sink writer's own worker threads; the Write methods only queue.
/// </summary>
internal sealed unsafe class Mp4Writer
{
    private readonly bool _hasAudio;
    private readonly uint _frameBytes;
    private IntPtr _sinkWriter;
    private IntPtr _deviceManager;
    private uint _videoStream;
    private uint _audioStream;
    private bool _platformStarted;
    private int _failureReported;

    /// <summary>Full path of the file being written.</summary>
    public string FilePath { get; }

    /// <summary>Raised (on the writing thread) the first time the encoder rejects a sample: disk full, encoder error...</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// Creates the file and the encoder pipeline. <paramref name="inputSize"/> is the size of the
    /// textures that will be written; the video is scaled on the GPU to <paramref name="outputSize"/>.
    /// Throws (leaving no file behind) if the pipeline cannot be built.
    /// </summary>
    public Mp4Writer(string filePath, D3DDevice device, Size inputSize, Size outputSize, int framesPerSecond, uint videoBitrate, bool hasAudio)
    {
        FilePath = MakeUnique(filePath);
        _hasAudio = hasAudio;
        _frameBytes = (uint)(inputSize.Width * inputSize.Height * 4);

        IntPtr attributes = IntPtr.Zero;
        try
        {
            MF.Check(MF.Startup());
            _platformStarted = true;

            // Hand our D3D device to Media Foundation so converter and encoder work on GPU textures.
            MF.Check(MF.MFCreateDXGIDeviceManager(out uint resetToken, out _deviceManager));
            MF.ResetDevice(_deviceManager, device.NativeDevice, resetToken);

            MF.Check(MF.MFCreateAttributes(out attributes, 3));
            MF.SetUInt32(attributes, MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            MF.SetUnknown(attributes, MF.MF_SINK_WRITER_D3D_MANAGER, _deviceManager);
            // Live capture must never be blocked by the writer; back-pressure is handled by the
            // recorder's fixed-size texture pool instead.
            MF.SetUInt32(attributes, MF.MF_SINK_WRITER_DISABLE_THROTTLING, 1);

            MF.Check(MF.MFCreateSinkWriterFromURL(FilePath, IntPtr.Zero, attributes, out _sinkWriter));

            // Note: adding the video stream is where Windows locates and initializes the hardware
            // encoder, which can take a second or two on some drivers.
            AddVideoStream(inputSize, outputSize, framesPerSecond, videoBitrate);
            if (hasAudio)
            {
                AddAudioStream();
            }

            MF.BeginWriting(_sinkWriter);
        }
        catch
        {
            Release();
            TryDeleteFile();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                Marshal.Release(attributes);
            }
        }
    }

    private void AddVideoStream(Size inputSize, Size outputSize, int framesPerSecond, uint bitrate)
    {
        // What goes into the file.
        IntPtr outputType = CreateMediaType(MF.MFMediaType_Video, MF.MFVideoFormat_H264);
        IntPtr inputType = CreateMediaType(MF.MFMediaType_Video, MF.MFVideoFormat_ARGB32);
        IntPtr encoderSettings = IntPtr.Zero;
        try
        {
            MF.SetUInt32(outputType, MF.MF_MT_AVG_BITRATE, bitrate);
            MF.SetUInt32(outputType, MF.MF_MT_MPEG2_PROFILE, MF.eAVEncH264VProfile_High);
            SetVideoGeometry(outputType, outputSize, framesPerSecond);
            _videoStream = MF.AddStream(_sinkWriter, outputType);

            // What we deliver: BGRA textures at capture size (the writer adds a GPU scaler/converter).
            SetVideoGeometry(inputType, inputSize, framesPerSecond);
            MF.SetUInt32(inputType, MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            // A key frame every two seconds keeps seeking in the finished video responsive.
            MF.Check(MF.MFCreateAttributes(out encoderSettings, 1));
            MF.SetUInt32(encoderSettings, MF.CODECAPI_AVEncMPVGOPSize, (uint)(framesPerSecond * 2));

            MF.SetInputMediaType(_sinkWriter, _videoStream, inputType, encoderSettings);
        }
        finally
        {
            Marshal.Release(outputType);
            Marshal.Release(inputType);
            if (encoderSettings != IntPtr.Zero)
            {
                Marshal.Release(encoderSettings);
            }
        }
    }

    private static void SetVideoGeometry(IntPtr mediaType, Size size, int framesPerSecond)
    {
        MF.SetUInt32(mediaType, MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
        MF.SetUInt64(mediaType, MF.MF_MT_FRAME_SIZE, (uint)size.Width, (uint)size.Height);
        MF.SetUInt64(mediaType, MF.MF_MT_FRAME_RATE, (uint)framesPerSecond, 1);
        MF.SetUInt64(mediaType, MF.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    }

    private void AddAudioStream()
    {
        const uint rate = AudioCapture.SampleRate;
        const uint channels = AudioCapture.Channels;

        IntPtr outputType = CreateMediaType(MF.MFMediaType_Audio, MF.MFAudioFormat_AAC);
        IntPtr inputType = CreateMediaType(MF.MFMediaType_Audio, MF.MFAudioFormat_PCM);
        try
        {
            MF.SetUInt32(outputType, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MF.SetUInt32(outputType, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            MF.SetUInt32(outputType, MF.MF_MT_AUDIO_NUM_CHANNELS, channels);
            MF.SetUInt32(outputType, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24_000); // 192 kbit/s AAC
            _audioStream = MF.AddStream(_sinkWriter, outputType);

            MF.SetUInt32(inputType, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MF.SetUInt32(inputType, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            MF.SetUInt32(inputType, MF.MF_MT_AUDIO_NUM_CHANNELS, channels);
            MF.SetUInt32(inputType, MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * sizeof(short));
            MF.SetUInt32(inputType, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * channels * sizeof(short));
            MF.SetUInt32(inputType, MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            MF.SetInputMediaType(_sinkWriter, _audioStream, inputType, IntPtr.Zero);
        }
        finally
        {
            Marshal.Release(outputType);
            Marshal.Release(inputType);
        }
    }

    private static IntPtr CreateMediaType(in Guid majorType, in Guid subtype)
    {
        MF.Check(MF.MFCreateMediaType(out IntPtr mediaType));
        MF.SetGuid(mediaType, MF.MF_MT_MAJOR_TYPE, majorType);
        MF.SetGuid(mediaType, MF.MF_MT_SUBTYPE, subtype);
        return mediaType;
    }

    /// <summary>
    /// Queues one video frame. The texture is referenced, not copied; it must stay untouched until
    /// the encoder releases it (the texture pool tracks that through the COM reference count).
    /// Times are in 100 ns units. Returns false once the writer has failed.
    /// </summary>
    public bool WriteVideo(IntPtr texture, long timestamp, long duration)
    {
        if (_failureReported != 0)
        {
            return false;
        }

        IntPtr buffer = IntPtr.Zero;
        IntPtr sample = IntPtr.Zero;
        try
        {
            MF.Check(MF.MFCreateDXGISurfaceBuffer(in MF.IID_ID3D11Texture2D, texture, 0, false, out buffer));
            MF.SetCurrentLength(buffer, _frameBytes); // surface buffers report length 0 unless told otherwise
            MF.Check(MF.MFCreateSample(out sample));
            MF.AddBuffer(sample, buffer);
            MF.SetSampleTiming(sample, timestamp, duration);
            MF.Check(MF.WriteSample(_sinkWriter, _videoStream, sample));
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return false;
        }
        finally
        {
            ReleaseIfSet(sample);
            ReleaseIfSet(buffer);
        }
    }

    /// <summary>
    /// Queues a block of 16-bit PCM audio (copied, so the caller may reuse its array).
    /// Times are in 100 ns units. Returns false once the writer has failed.
    /// </summary>
    public bool WriteAudio(byte[] pcm, int byteCount, long timestamp, long duration)
    {
        if (!_hasAudio || _failureReported != 0)
        {
            return false;
        }

        IntPtr buffer = IntPtr.Zero;
        IntPtr sample = IntPtr.Zero;
        try
        {
            MF.Check(MF.MFCreateMemoryBuffer((uint)byteCount, out buffer));
            byte* target = MF.LockBuffer(buffer);
            pcm.AsSpan(0, byteCount).CopyTo(new Span<byte>(target, byteCount));
            MF.UnlockBuffer(buffer);
            MF.SetCurrentLength(buffer, (uint)byteCount);

            MF.Check(MF.MFCreateSample(out sample));
            MF.AddBuffer(sample, buffer);
            MF.SetSampleTiming(sample, timestamp, duration);
            MF.Check(MF.WriteSample(_sinkWriter, _audioStream, sample));
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return false;
        }
        finally
        {
            ReleaseIfSet(sample);
            ReleaseIfSet(buffer);
        }
    }

    /// <summary>
    /// Flushes the encoders and finalizes the MP4 (the index that makes the file playable is written
    /// now). Call only after all producers have stopped. Throws if the file could not be completed.
    /// </summary>
    public Task FinishAsync() => Task.Run(() =>
    {
        try
        {
            MF.Check(MF.FinalizeWriter(_sinkWriter));
        }
        finally
        {
            Release();
        }
    });

    /// <summary>Abandons the recording and removes the partial file (used when starting fails).</summary>
    public void Abort()
    {
        Release();
        TryDeleteFile();
    }

    private void ReportFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) == 0)
        {
            Failed?.Invoke(error);
        }
    }

    private void Release()
    {
        ReleaseIfSet(_sinkWriter);
        _sinkWriter = IntPtr.Zero;
        ReleaseIfSet(_deviceManager);
        _deviceManager = IntPtr.Zero;

        if (_platformStarted)
        {
            _platformStarted = false;
            MF.MFShutdown();
        }
    }

    private static void ReleaseIfSet(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
        }
    }

    private void TryDeleteFile()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Best effort cleanup.
        }
    }

    /// <summary>Never overwrite an existing recording: append a counter if the name is taken.</summary>
    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
