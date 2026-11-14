using System.Runtime.InteropServices;
using LiteRec.Interop;

namespace LiteRec.Audio;

/// <summary>
/// One WASAPI capture stream (a microphone, or the loopback of a playback device) feeding a small
/// jitter buffer. All methods except the <see cref="Gain"/> setter are called from the audio thread
/// only, so no locking is needed.
/// </summary>
internal sealed unsafe class WasapiSource : IDisposable
{
    private const int Channels = AudioCapture.Channels;

    // Jitter buffer tuning, in sample frames at 48 kHz.
    private const int CushionFrames = AudioCapture.SampleRate * 30 / 1000;     // refill level after running dry
    private const int MaxBufferedFrames = AudioCapture.SampleRate * 250 / 1000; // beyond this we are lagging: catch up
    private const int CapacityFrames = AudioCapture.SampleRate;                 // ring size: 1 second

    private readonly float[] _ring = new float[CapacityFrames * Channels];
    private IntPtr _audioClient;
    private IntPtr _captureClient;
    private int _readIndex;   // in samples
    private int _count;       // buffered samples
    private bool _priming = true;
    private bool _failed;
    private volatile float _gain = 1f;

    /// <summary>Linear volume applied while mixing (0 = muted).</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    private WasapiSource(IntPtr audioClient, IntPtr captureClient)
    {
        _audioClient = audioClient;
        _captureClient = captureClient;
    }

    /// <summary>Opens the device (empty id = system default). Throws a COMException on failure.</summary>
    public static WasapiSource Open(string deviceId, bool loopback)
    {
        CoreAudio.OpenCaptureStream(deviceId, loopback ? CoreAudio.DataFlow.Render : CoreAudio.DataFlow.Capture,
            loopback, AudioCapture.SampleRate, Channels, out IntPtr audioClient, out IntPtr captureClient);
        return new WasapiSource(audioClient, captureClient);
    }

    public void Start() => Marshal.ThrowExceptionForHR(CoreAudio.Start(_audioClient));

    public void Stop()
    {
        if (_audioClient != IntPtr.Zero)
        {
            CoreAudio.Stop(_audioClient);
        }
    }

    /// <summary>Moves every packet the device has ready into the jitter buffer.</summary>
    public void Drain()
    {
        if (_failed)
        {
            return;
        }

        while (true)
        {
            if (CoreAudio.GetNextPacketSize(_captureClient, out uint pending) < 0)
            {
                // Device unplugged or disabled: this source goes silent, the recording carries on.
                _failed = true;
                return;
            }

            if (pending == 0)
            {
                return;
            }

            if (CoreAudio.GetBuffer(_captureClient, out float* data, out uint frames, out uint flags) < 0)
            {
                _failed = true;
                return;
            }

            bool silent = (flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == null;
            Write(silent ? null : data, (int)frames * Channels);
            CoreAudio.ReleaseBuffer(_captureClient, frames);
        }
    }

    /// <summary>
    /// Adds up to <paramref name="frames"/> frames of this source to <paramref name="mix"/>. A
    /// loopback stream delivers nothing while no application is playing sound, so an empty buffer
    /// is normal and simply contributes silence.
    /// </summary>
    public void MixInto(float[] mix, int frames)
    {
        if (_priming)
        {
            // After running dry, wait for a small cushion before resuming so that normal packet
            // jitter does not cause an audible stutter.
            if (_count < CushionFrames * Channels)
            {
                return;
            }

            _priming = false;
        }

        if (_count > MaxBufferedFrames * Channels)
        {
            // The device clock runs faster than ours (or we stalled): skip ahead to stay in sync.
            Skip(_count - CushionFrames * Channels);
        }

        int samples = Math.Min(frames * Channels, _count);
        float gain = _gain;
        if (gain > 0f)
        {
            int index = _readIndex;
            for (int i = 0; i < samples; i++)
            {
                mix[i] += _ring[index] * gain;
                if (++index == _ring.Length)
                {
                    index = 0;
                }
            }
        }

        Skip(samples);
        if (samples < frames * Channels)
        {
            _priming = true; // ran dry
        }
    }

    private void Write(float* data, int samples)
    {
        if (samples > _ring.Length)
        {
            return; // cannot happen with a 200 ms device buffer; guard anyway
        }

        int overflow = _count + samples - _ring.Length;
        if (overflow > 0)
        {
            Skip(overflow); // reader stalled for a full second: drop the oldest audio
        }

        int writeIndex = (_readIndex + _count) % _ring.Length;
        int firstPart = Math.Min(samples, _ring.Length - writeIndex);
        if (data == null)
        {
            Array.Clear(_ring, writeIndex, firstPart);
            Array.Clear(_ring, 0, samples - firstPart);
        }
        else
        {
            new ReadOnlySpan<float>(data, firstPart).CopyTo(_ring.AsSpan(writeIndex));
            new ReadOnlySpan<float>(data + firstPart, samples - firstPart).CopyTo(_ring);
        }

        _count += samples;
    }

    private void Skip(int samples)
    {
        _readIndex = (_readIndex + samples) % _ring.Length;
        _count -= samples;
    }

    public void Dispose()
    {
        if (_captureClient != IntPtr.Zero)
        {
            Marshal.Release(_captureClient);
            _captureClient = IntPtr.Zero;
        }

        if (_audioClient != IntPtr.Zero)
        {
            CoreAudio.Stop(_audioClient);
            Marshal.Release(_audioClient);
            _audioClient = IntPtr.Zero;
        }
    }
}
