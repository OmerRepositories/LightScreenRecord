namespace LiteRec.Interop;

/// <summary>
/// Sleeps with sub-millisecond accuracy using a high resolution waitable timer (Windows 10 1803+).
/// Unlike timeBeginPeriod(1) this does not raise the global system timer rate, so it costs no extra
/// power or CPU for the rest of the machine. The thread truly sleeps; there is no busy waiting.
/// </summary>
internal sealed class PrecisionTimer : IDisposable
{
    private IntPtr _handle;

    public PrecisionTimer()
    {
        _handle = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
            NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);

        if (_handle == IntPtr.Zero)
        {
            // Older Windows 10 builds: fall back to a normal waitable timer (~1-15 ms granularity).
            _handle = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, NativeMethods.TIMER_ALL_ACCESS);
        }
    }

    /// <summary>Blocks the calling thread for the given duration, expressed in 100 ns ticks.</summary>
    public void Wait(long ticks)
    {
        if (ticks <= 0)
        {
            return;
        }

        long dueTime = -ticks; // negative = relative time
        if (_handle != IntPtr.Zero && NativeMethods.SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            NativeMethods.WaitForSingleObject(_handle, NativeMethods.INFINITE);
        }
        else
        {
            Thread.Sleep((int)Math.Max(1, ticks / TimeSpan.TicksPerMillisecond));
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
