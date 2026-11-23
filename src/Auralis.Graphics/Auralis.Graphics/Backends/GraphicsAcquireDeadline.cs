using System.Diagnostics;

namespace Auralis.Graphics.Backends;

/// <summary>
/// One monotonic image-acquisition deadline shared by context-gate contention and native sync waits.
/// Infinite waits use bounded native client-wait intervals rather than a GL timeout sentinel.
/// </summary>
internal readonly struct GraphicsAcquireDeadline
{
    internal const ulong InfiniteClientWaitSliceNanoseconds = 100_000_000;

    private readonly bool _infinite;
    private readonly long _deadlineTimestamp;
    private readonly long _frequency;
    private readonly Func<long>? _timestampProvider;

    internal GraphicsAcquireDeadline(TimeSpan timeout)
        : this(timeout, Stopwatch.GetTimestamp(), Stopwatch.Frequency, null)
    {
    }

    /// <summary>Creates a deadline using a controllable monotonic timestamp provider for deterministic tests.</summary>
    internal GraphicsAcquireDeadline(TimeSpan timeout, Func<long> timestampProvider)
        : this(
            timeout,
            (timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider)))(),
            Stopwatch.Frequency,
            timestampProvider)
    {
    }

    internal GraphicsAcquireDeadline(TimeSpan timeout, long startTimestamp, long frequency)
        : this(timeout, startTimestamp, frequency, null)
    {
    }

    private GraphicsAcquireDeadline(
        TimeSpan timeout,
        long startTimestamp,
        long frequency,
        Func<long>? timestampProvider)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        _infinite = timeout == Timeout.InfiniteTimeSpan;
        _frequency = frequency;
        _timestampProvider = timestampProvider;
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
    internal bool IsExpired => IsExpiredAt(GetTimestamp());
    internal bool IsExpiredAt(long timestamp) => !_infinite && timestamp >= _deadlineTimestamp;

    /// <summary>Gets the remaining context-gate timeout without extending the original deadline.</summary>
    internal TimeSpan GetRemainingTime() => GetRemainingTimeAt(GetTimestamp());

    internal TimeSpan GetRemainingTimeAt(long timestamp)
    {
        if (_infinite)
            return Timeout.InfiniteTimeSpan;

        long remaining = _deadlineTimestamp - timestamp;
        if (remaining <= 0)
            return TimeSpan.Zero;

        // Round up to one TimeSpan tick when a positive budget remains, avoiding an early
        // zero-timeout context attempt caused only by conversion precision.
        UInt128 ticks = ((UInt128)(ulong)remaining * (ulong)TimeSpan.TicksPerSecond +
                         (ulong)_frequency - 1) / (ulong)_frequency;
        return TimeSpan.FromTicks(ticks >= (UInt128)long.MaxValue ? long.MaxValue : (long)ticks);
    }

    /// <summary>
    /// Gets a native client-wait timeout. Infinite waits use a finite retry interval; finite waits
    /// remain distinct from the all-bits-one timeout sentinel.
    /// </summary>
    internal ulong GetClientWaitTimeoutNanoseconds() =>
        GetClientWaitTimeoutNanosecondsAt(GetTimestamp());

    private long GetTimestamp() => _timestampProvider?.Invoke() ?? Stopwatch.GetTimestamp();

    internal ulong GetClientWaitTimeoutNanosecondsAt(long timestamp)
    {
        if (_infinite)
            return InfiniteClientWaitSliceNanoseconds;

        long remaining = _deadlineTimestamp - timestamp;
        if (remaining <= 0)
            return 0;

        UInt128 nanoseconds = (UInt128)(ulong)remaining * 1_000_000_000UL;
        nanoseconds = (nanoseconds + (ulong)_frequency - 1) / (ulong)_frequency;
        const ulong largestFiniteClientWait = ulong.MaxValue - 1;
        return nanoseconds >= (UInt128)largestFiniteClientWait
            ? largestFiniteClientWait
            : (ulong)nanoseconds;
    }
}
