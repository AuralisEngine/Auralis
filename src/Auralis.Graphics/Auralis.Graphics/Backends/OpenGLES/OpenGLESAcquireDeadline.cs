using System.Diagnostics;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// One monotonic image-acquisition deadline. Infinite acquisition uses bounded native client waits
/// instead of passing GL_TIMEOUT_IGNORED to ClientWaitSync.
/// </summary>
internal readonly struct OpenGLESAcquireDeadline
{
    internal const ulong InfiniteClientWaitSliceNanoseconds = 100_000_000;

    private readonly bool _infinite;
    private readonly long _deadlineTimestamp;
    private readonly long _frequency;

    internal OpenGLESAcquireDeadline(TimeSpan timeout)
        : this(timeout, Stopwatch.GetTimestamp(), Stopwatch.Frequency)
    {
    }

    internal OpenGLESAcquireDeadline(TimeSpan timeout, long startTimestamp, long frequency)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        _infinite = timeout == Timeout.InfiniteTimeSpan;
        _frequency = frequency;
        if (_infinite)
        {
            _deadlineTimestamp = long.MaxValue;
            return;
        }

        UInt128 scaled = (UInt128)(ulong)timeout.Ticks * (ulong)frequency;
        UInt128 delta = (scaled + (ulong)TimeSpan.TicksPerSecond - 1) /
                        (ulong)TimeSpan.TicksPerSecond;
        long available = startTimestamp >= 0 ? long.MaxValue - startTimestamp : long.MaxValue;
        _deadlineTimestamp = delta >= (UInt128)(ulong)available
            ? long.MaxValue
            : startTimestamp + (long)delta;
    }

    internal bool IsInfinite => _infinite;
    internal bool IsExpired => IsExpiredAt(Stopwatch.GetTimestamp());
    internal bool IsExpiredAt(long timestamp) => !_infinite && timestamp >= _deadlineTimestamp;

    /// <summary>Gets the remaining context-gate timeout without extending the original deadline.</summary>
    internal TimeSpan GetRemainingTime() => GetRemainingTimeAt(Stopwatch.GetTimestamp());

    internal TimeSpan GetRemainingTimeAt(long timestamp)
    {
        if (_infinite)
            return Timeout.InfiniteTimeSpan;

        long remaining = _deadlineTimestamp - timestamp;
        if (remaining <= 0)
            return TimeSpan.Zero;

        UInt128 ticks = (UInt128)(ulong)remaining * (ulong)TimeSpan.TicksPerSecond /
                        (ulong)_frequency;
        return ticks == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(ticks >= (UInt128)long.MaxValue ? long.MaxValue : (long)ticks);
    }

    /// <summary>
    /// Gets a GLES ClientWaitSync timeout. Finite waits use the current remaining deadline;
    /// infinite waits use a supported finite interval and retry after TIMEOUT_EXPIRED.
    /// </summary>
    internal ulong GetClientWaitTimeoutNanoseconds() =>
        GetClientWaitTimeoutNanosecondsAt(Stopwatch.GetTimestamp());

    internal ulong GetClientWaitTimeoutNanosecondsAt(long timestamp)
    {
        if (_infinite)
            return InfiniteClientWaitSliceNanoseconds;

        long remaining = _deadlineTimestamp - timestamp;
        if (remaining <= 0)
            return 0;

        UInt128 nanoseconds = (UInt128)(ulong)remaining * 1_000_000_000UL /
                              (ulong)_frequency;
        return nanoseconds >= (UInt128)ulong.MaxValue ? ulong.MaxValue : (ulong)nanoseconds;
    }
}
