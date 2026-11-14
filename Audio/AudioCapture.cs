using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteRec.Interop;

namespace LiteRec.Audio;

/// <summary>
/// Captures system audio (WASAPI loopback) and/or a microphone and mixes them into one 48 kHz
/// stereo stream on a dedicated audio thread. The thread wakes every 10 ms, empties the device
/// buffers and emits exactly as many samples as real time has advanced, so the output is continuous
/// even while nothing is playing (loopback alone would leave holes there).
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private const int TickMilliseconds = 10;
    private const int MaxFramesPerTick = SampleRate / 10; // never emit more than 100 ms in one block

    private readonly WasapiSource? _system;
    private readonly WasapiSource? _microphone;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>
    /// Raised on the audio thread for every block of mixed audio. Arguments: a buffer of 16-bit
    /// interleaved PCM (reused between calls, so only valid during the callback) and the number of
    /// sample frames in it.
    /// </summary>
    public event Action<byte[], int>? AudioAvailable;

    private AudioCapture(WasapiSource? system, WasapiSource? microphone)
    {
        _system = system;
        _microphone = microphone;
    }

    /// <summary>
    /// Opens the requested sources. Pass null to leave a source out entirely, or an empty string to
    /// use the system default device. Throws with a readable message if a device cannot be opened.
    /// </summary>
    public static Task<AudioCapture> CreateAsync(string? systemDeviceId, string? microphoneDeviceId)
    {
        // Device activation can take a moment (driver start-up), so keep it off the UI thread.
        return Task.Run(() =>
        {
            WasapiSource? system = null;
            WasapiSource? microphone = null;
            try
            {
                if (systemDeviceId is not null)
                {
                    system = Open(systemDeviceId, loopback: true, "system audio");
                }

                if (microphoneDeviceId is not null)
                {
                    microphone = Open(microphoneDeviceId, loopback: false, "microphone");
                }

                return new AudioCapture(system, microphone);
            }
            catch
            {
                system?.Dispose();
                microphone?.Dispose();
                throw;
            }
        });
    }

    private static WasapiSource Open(string deviceId, bool loopback, string kind)
    {
        try
        {
            return WasapiSource.Open(deviceId, loopback);
        }
        catch (Exception ex)
        {
            string reason = ex.HResult switch
            {
                CoreAudio.E_NOTFOUND => "no such device is connected",
                CoreAudio.E_ACCESSDENIED => "access denied - allow desktop apps to use the microphone in Windows privacy settings",
                CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED => "the device was disconnected",
                _ => $"error 0x{ex.HResult:X8}",
            };
            throw new InvalidOperationException($"Could not open the {kind} device ({reason}).", ex);
        }
    }

    /// <summary>Linear gain of the system audio source (0 = muted, 1 = unchanged). Safe to change while recording.</summary>
    public void SetSystemGain(double gain)
    {
        if (_system is not null)
        {
            _system.Gain = (float)gain;
        }
    }

    /// <summary>Linear gain of the microphone source (0 = muted, 1 = unchanged). Safe to change while recording.</summary>
    public void SetMicrophoneGain(double gain)
    {
        if (_microphone is not null)
        {
            _microphone.Gain = (float)gain;
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _system?.Start();
        _microphone?.Start();

        _running = true;
        _thread = new Thread(MixLoop)
        {
            Name = "LiteRec audio",
            IsBackground = true,
            Priority = ThreadPriority.Highest, // tiny, time-critical workload
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
        _system?.Stop();
        _microphone?.Stop();
    }

    private void MixLoop()
    {
        using var timer = new PrecisionTimer();
        var mix = new float[MaxFramesPerTick * Channels];
        var pcm = new byte[MaxFramesPerTick * Channels * sizeof(short)]; // reused for every block: no garbage
        var clock = Stopwatch.StartNew();
        long framesEmitted = 0;

        bool errorLogged = false;
        while (_running)
        {
            try
            {
                MixTick();
            }
            catch (Exception ex)
            {
                // An exception here (device removed, driver hiccup) would kill the whole process.
                // Skip this tick; the recorder pads any gap with silence.
                if (!errorLogged)
                {
                    errorLogged = true;
                    CrashLog.Write("Audio thread error (recording continues)", ex);
                }
            }
        }

        void MixTick()
        {
            timer.Wait(TickMilliseconds * TimeSpan.TicksPerMillisecond);

            _system?.Drain();
            _microphone?.Drain();

            // Emit as many frames as real time has advanced since the last tick.
            long framesDue = clock.ElapsedTicks * SampleRate / Stopwatch.Frequency;
            long pending = framesDue - framesEmitted;
            if (pending <= 0)
            {
                return;
            }

            if (pending > MaxFramesPerTick)
            {
                // The thread was held up for a long time; skip the backlog (the recorder pads the
                // timeline with silence) instead of emitting a burst.
                framesEmitted = framesDue - MaxFramesPerTick;
                pending = MaxFramesPerTick;
            }

            int frames = (int)pending;
            int samples = frames * Channels;
            Array.Clear(mix, 0, samples);
            _system?.MixInto(mix, frames);
            _microphone?.MixInto(mix, frames);
            framesEmitted += frames;

            ToPcm16(mix, pcm, samples);
            AudioAvailable?.Invoke(pcm, frames);
        }
    }

    /// <summary>Converts float samples in [-1, 1] to 16-bit PCM (what the AAC encoder consumes), with clipping.</summary>
    private static void ToPcm16(float[] source, byte[] pcm, int samples)
    {
        Span<short> target = MemoryMarshal.Cast<byte, short>(pcm);
        for (int i = 0; i < samples; i++)
        {
            float value = source[i] * 32767f;
            target[i] = value >= 32767f ? short.MaxValue : value <= -32768f ? short.MinValue : (short)value;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _system?.Dispose();
        _microphone?.Dispose();
    }
}
