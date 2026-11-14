using System.Diagnostics;

namespace LiteRec.Recording;

/// <summary>
/// The recording's master clock: it advances only while recording and stands still while paused.
/// Video and audio timestamps are both derived from it, so a pause leaves no gap in the output file.
/// </summary>
internal sealed class PausableClock
{
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = new();

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _stopwatch.IsRunning;
            }
        }
    }

    /// <summary>Recorded time so far, in 100 ns ticks.</summary>
    public long ElapsedTicks
    {
        get
        {
            lock (_lock)
            {
                return _stopwatch.Elapsed.Ticks;
            }
        }
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(ElapsedTicks);

    public void Start()
    {
        lock (_lock)
        {
            _stopwatch.Start();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _stopwatch.Stop();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _stopwatch.Reset();
        }
    }
}
