using System.Runtime.InteropServices;
using LiteRec.Audio;
using LiteRec.Capture;
using LiteRec.Encoding;
using LiteRec.Interop;

namespace LiteRec.Recording;

internal enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
}

/// <summary>
/// Orchestrates one recording and owns its threading model:
///   * video thread (this class)   - paces frames at the target rate and copies them on the GPU
///   * audio thread (AudioCapture) - reads WASAPI and delivers mixed PCM blocks
///   * encoder/mux threads (Media Foundation sink writer) - encode both streams and write the MP4
/// The UI thread only starts/stops things and never touches frame or audio data.
///
/// Public methods must be called from the UI thread; <see cref="StateChanged"/> is raised there too.
/// </summary>
internal sealed class Recorder
{
    // Frame textures shared with the encoder: the last picture, one or two in flight, one free.
    // Each costs width x height x 4 bytes of video memory, so the pool is kept deliberately small.
    private const int TexturePoolSize = 4;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const int AudioBytesPerFrame = AudioCapture.Channels * sizeof(short);

    private readonly PausableClock _clock = new();
    private readonly ManualResetEventSlim _runGate = new(false); // closed while paused
    private readonly object _audioLock = new();
    private readonly object _pauseLock = new();
    private bool _paused; // guarded by _pauseLock
    private readonly byte[] _silence = new byte[AudioCapture.SampleRate / 2 * AudioBytesPerFrame]; // 0.5 s of silence

    private D3DDevice? _device;
    private ScreenCapturer? _capturer;
    private TexturePool? _pool;
    private AudioCapture? _audio;
    private Mp4Writer? _writer;
    private Thread? _videoThread;

    private int _framesPerSecond;
    private volatile bool _stopRequested;
    private long _audioFramesWritten;
    private long _framesWritten;
    private long _framesDropped;
    private int _interruptRaised;

    public RecorderState State { get; private set; } = RecorderState.Idle;

    /// <summary>Recorded time (excludes paused periods).</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Frame slots skipped because capture or encoding could not keep up.</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event Action<RecorderState>? StateChanged;

    /// <summary>
    /// Raised on a worker thread when the recording cannot continue (recorded window closed, encoder
    /// failure). The argument is null for a closed target, otherwise the error text. The listener is
    /// expected to call <see cref="StopAsync"/> on the UI thread.
    /// </summary>
    public event Action<string?>? Interrupted;

    /// <summary>Starts a recording. Throws (after cleaning up) if any part fails to initialize.</summary>
    public async Task StartAsync(RecordingOptions options)
    {
        if (State != RecorderState.Idle)
        {
            return;
        }

        SetState(RecorderState.Starting);
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);

            _framesPerSecond = options.FramesPerSecond;
            _stopRequested = false;
            _audioFramesWritten = 0;
            _framesWritten = 0;
            _framesDropped = 0;
            _interruptRaised = 0;
            _clock.Reset();

            // Video capture.
            _device = D3DDevice.Create();
            _capturer = new ScreenCapturer(_device, options.Target, options.CaptureCursor);
            _capturer.TargetClosed += OnTargetClosed;
            Size frameSize = _capturer.FrameSize;
            Size outputSize = GetOutputSize(frameSize, options.MaxHeight);

            // Audio capture (optional).
            bool hasAudio = options.SystemAudioDeviceId is not null || options.MicrophoneDeviceId is not null;
            if (hasAudio)
            {
                _audio = await AudioCapture.CreateAsync(options.SystemAudioDeviceId, options.MicrophoneDeviceId);
                _audio.SetSystemGain(options.SystemAudioGain);
                _audio.SetMicrophoneGain(options.MicrophoneGain);
                _audio.AudioAvailable += OnAudioAvailable;
            }

            // Encoder. Loading the hardware encoder takes a moment, so build it off the UI thread.
            string path = Path.Combine(options.OutputDirectory, $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            uint bitrate = GetVideoBitrate(outputSize, options.FramesPerSecond, options.Quality);
            D3DDevice device = _device;
            _writer = await Task.Run(() => new Mp4Writer(path, device, frameSize, outputSize, options.FramesPerSecond, bitrate, hasAudio));
            _writer.Failed += OnWriterFailed;

            _pool = new TexturePool(_device, frameSize, TexturePoolSize);

            // Wait briefly for the first captured frame so the video does not open with a black frame.
            _capturer.Start();
            ScreenCapturer capturer = _capturer;
            await Task.Run(() => capturer.WaitForFirstFrame(1000));

            // Keep the PC and display awake for the duration of the recording.
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

            // The master clock is started by the video thread itself, right before its first frame,
            // so thread start-up time never shows up as a skipped frame.
            _paused = false;
            _audio?.Start();
            _runGate.Set();
            _videoThread = new Thread(VideoLoop)
            {
                Name = "LiteRec video capture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _videoThread.SetApartmentState(ApartmentState.MTA);
            _videoThread.Start();

            SetState(RecorderState.Recording);
        }
        catch
        {
            _writer?.Abort();
            Cleanup();
            SetState(RecorderState.Idle);
            throw;
        }
    }

    /// <summary>Pauses a running recording or resumes a paused one.</summary>
    public void TogglePause()
    {
        if (State == RecorderState.Recording)
        {
            lock (_pauseLock)
            {
                _paused = true;
                _runGate.Reset();
                _clock.Pause();
            }

            SetState(RecorderState.Paused);
        }
        else if (State == RecorderState.Paused)
        {
            lock (_pauseLock)
            {
                _paused = false;
                _clock.Start();
                _runGate.Set();
            }

            SetState(RecorderState.Recording);
        }
    }

    /// <summary>Live volume control; safe to call at any time.</summary>
    public void SetAudioGains(double systemGain, double microphoneGain)
    {
        _audio?.SetSystemGain(systemGain);
        _audio?.SetMicrophoneGain(microphoneGain);
    }

    /// <summary>
    /// Stops the recording and finalizes the file. Returns the saved file path, or null if nothing
    /// was being recorded. Throws if the encoder reported an error (the file may then be incomplete).
    /// </summary>
    public async Task<string?> StopAsync()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
        {
            return null;
        }

        SetState(RecorderState.Stopping);

        // Stop the producers first, then let the encoder drain what is already queued.
        _stopRequested = true;
        _clock.Pause();
        _runGate.Set();
        Thread? videoThread = _videoThread;
        AudioCapture? audio = _audio;
        await Task.Run(() =>
        {
            videoThread?.Join();
            audio?.Stop(); // joins the audio thread
        });

        string path = _writer!.FilePath;
        try
        {
            await _writer.FinishAsync();
        }
        finally
        {
            Cleanup();
            SetState(RecorderState.Idle);
        }

        return path;
    }

    // ---------------------------------------------------------------------------------------------
    // Video thread
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Emits exactly one frame per frame slot of the master clock (constant frame rate). When the
    /// screen has not changed the previous texture is re-submitted without any GPU copy, so a static
    /// screen costs next to nothing.
    /// </summary>
    private void VideoLoop()
    {
        using var timer = new PrecisionTimer();
        ScreenCapturer capturer = _capturer!;
        TexturePool pool = _pool!;
        Mp4Writer writer = _writer!;
        int fps = _framesPerSecond;
        long frameDuration = TicksPerSecond / fps;

        long frameIndex = 0;
        int lastTexture = 0; // texture 0 starts out black and is used until the first frame arrives

        try
        {
            lock (_pauseLock)
            {
                if (!_paused)
                {
                    _clock.Start(); // time zero of the recording
                }
            }

            while (!_stopRequested)
            {
                if (!_runGate.Wait(100))
                {
                    continue; // paused: sleep, wake occasionally to notice a stop request
                }

                long now = _clock.ElapsedTicks;
                long due = frameIndex * TicksPerSecond / fps;
                if (now < due)
                {
                    timer.Wait(due - now);
                    continue; // re-check state (a pause or stop may have happened while sleeping)
                }

                // If we overslept by whole frames (system stall), skip those slots instead of bursting.
                long currentSlot = now * fps / TicksPerSecond;
                if (currentSlot > frameIndex)
                {
                    Interlocked.Add(ref _framesDropped, currentSlot - frameIndex);
                    frameIndex = currentSlot;
                }

                // Copy the newest screen image into a texture the encoder is not using. If the
                // screen did not change (or every texture is still busy) the last picture is repeated.
                int free = pool.AcquireFree(keep: lastTexture);
                if (free >= 0 && capturer.TryCopyLatestFrame(pool.GetTexture(free)))
                {
                    lastTexture = free;
                }

                if (pool.GetPendingUses(lastTexture) >= TexturePoolSize)
                {
                    // The encoder is far behind: drop this slot rather than queue unbounded work.
                    Interlocked.Increment(ref _framesDropped);
                }
                else if (writer.WriteVideo(pool.GetTexture(lastTexture), frameIndex * TicksPerSecond / fps, frameDuration))
                {
                    Interlocked.Increment(ref _framesWritten);
                }

                frameIndex++;
            }
        }
        catch (Exception ex)
        {
            RaiseInterrupted(ex.Message);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Audio thread
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Timestamps audio by counting samples (gap-free, as AAC requires) while keeping it locked to
    /// the master clock: silence is inserted if audio falls behind, and a block is dropped if it
    /// runs ahead. Blocks that arrive while paused are discarded.
    /// </summary>
    private void OnAudioAvailable(byte[] pcm, int frames)
    {
        if (!_clock.IsRunning || _stopRequested)
        {
            return;
        }

        Mp4Writer? writer = _writer;
        if (writer is null)
        {
            return;
        }

        lock (_audioLock)
        {
            const int rate = AudioCapture.SampleRate;
            long expected = _clock.ElapsedTicks * rate / TicksPerSecond; // where the stream should end after this block
            long tolerance = _audioFramesWritten == 0 ? 0 : rate / 10;     // align exactly at start, then allow 100 ms
            long afterBlock = _audioFramesWritten + frames;

            if (afterBlock > expected + rate / 10)
            {
                return; // ahead of the clock
            }

            long gap = expected - afterBlock;
            if (gap > tolerance)
            {
                // Behind the clock (first block after start, or a stall): pad with silence.
                int maxFramesPerBlock = _silence.Length / AudioBytesPerFrame;
                while (gap > 0)
                {
                    int block = (int)Math.Min(gap, maxFramesPerBlock);
                    WriteAudioBlock(writer, _silence, block);
                    gap -= block;
                }
            }

            WriteAudioBlock(writer, pcm, frames);
        }
    }

    private void WriteAudioBlock(Mp4Writer writer, byte[] pcm, int frames)
    {
        long timestamp = _audioFramesWritten * TicksPerSecond / AudioCapture.SampleRate;
        long duration = frames * TicksPerSecond / AudioCapture.SampleRate;
        writer.WriteAudio(pcm, frames * AudioBytesPerFrame, timestamp, duration);
        _audioFramesWritten += frames;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private void OnTargetClosed() => RaiseInterrupted(null);

    private void OnWriterFailed(Exception error) => RaiseInterrupted(error.Message);

    private void RaiseInterrupted(string? error)
    {
        // Only report the first problem; later ones are usually consequences of it.
        if (Interlocked.Exchange(ref _interruptRaised, 1) == 0)
        {
            Interrupted?.Invoke(error);
        }
    }

    private void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Cleanup()
    {
        SetThreadExecutionState(ES_CONTINUOUS);

        if (_capturer is not null)
        {
            _capturer.TargetClosed -= OnTargetClosed;
            _capturer.Dispose();
            _capturer = null;
        }

        if (_audio is not null)
        {
            _audio.AudioAvailable -= OnAudioAvailable;
            _audio.Dispose();
            _audio = null;
        }

        if (_writer is not null)
        {
            _writer.Failed -= OnWriterFailed;
            _writer = null;
        }

        _pool?.Dispose();
        _pool = null;
        _device?.Dispose();
        _device = null;
        _videoThread = null;
        _runGate.Reset();

        // The WinRT capture objects are thin managed wrappers around large GPU resources. Collect
        // them now so video memory is returned as soon as a recording ends, not at some later GC.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// Fits the captured picture into the chosen resolution (never enlarging it), keeps the
    /// dimensions even as H.264 requires, and stays within hardware encoder limits.
    /// </summary>
    private static Size GetOutputSize(Size frameSize, int maxHeight)
    {
        const int maxDimension = 4096;
        double scale = 1.0;
        if (maxHeight > 0 && frameSize.Height > maxHeight)
        {
            scale = (double)maxHeight / frameSize.Height;
        }

        scale = Math.Min(scale, Math.Min((double)maxDimension / frameSize.Width, (double)maxDimension / frameSize.Height));

        int width = Math.Max(16, (int)Math.Round(frameSize.Width * scale) & ~1);
        int height = Math.Max(16, (int)Math.Round(frameSize.Height * scale) & ~1);
        return new Size(width, height);
    }

    /// <summary>
    /// Picks a bitrate from the pixel rate. Screen content compresses well, so these values are modest;
    /// 60 fps gets about 1.5x (not 2x) the 30 fps bitrate because consecutive frames are more alike.
    /// </summary>
    private static uint GetVideoBitrate(Size size, int fps, VideoQuality quality)
    {
        double bitsPerPixel = quality switch
        {
            VideoQuality.SmallFile => 0.05,
            VideoQuality.High => 0.16,
            _ => 0.09,
        };

        double bitrate = size.Width * size.Height * bitsPerPixel * 30 * Math.Pow(fps / 30.0, 0.6);
        return (uint)Math.Clamp(bitrate, 400_000, 60_000_000);
    }

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
