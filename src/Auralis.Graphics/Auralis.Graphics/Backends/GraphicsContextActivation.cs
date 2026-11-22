using System.Diagnostics;

namespace Auralis.Graphics.Backends;

/// <summary>
/// Tracks the per-thread nesting depth of a device's current-context activation. Callers must hold
/// the device context gate while entering and exiting. Only the outermost acquisition changes the
/// native current context, and only the final release clears it.
/// </summary>
internal sealed class GraphicsContextActivation
{
    private readonly Action _makeCurrent;
    private readonly Action _releaseCurrent;
    private int _ownerThreadId;
    private int _depth;

    internal GraphicsContextActivation(Action makeCurrent, Action releaseCurrent)
    {
        _makeCurrent = makeCurrent ?? throw new ArgumentNullException(nameof(makeCurrent));
        _releaseCurrent = releaseCurrent ?? throw new ArgumentNullException(nameof(releaseCurrent));
    }

    internal void Enter()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (_depth != 0)
        {
            if (_ownerThreadId != threadId)
                throw new InvalidOperationException("The current-context activation is owned by another thread.");
            checked { _depth++; }
            return;
        }

        _makeCurrent();
        _ownerThreadId = threadId;
        _depth = 1;
    }

    internal void Exit()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (_depth == 0 || _ownerThreadId != threadId)
            throw new InvalidOperationException("The current-context activation must be released by its acquiring thread.");

        _depth--;
        if (_depth == 0)
        {
            _ownerThreadId = 0;
            _releaseCurrent();
        }
    }
}
/// <summary>Deadline-bounded acquisition for the device-wide context monitor.</summary>
internal static class GraphicsContextGate
{
    // Short timed waits keep cancellation responsive without spinning while the context is busy.
    private const int CancellationPollingIntervalMilliseconds = 1;

    internal static bool TryEnter(object gate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (gate is null)
            throw new ArgumentNullException(nameof(gate));
        ValidateTimeout(timeout);

        cancellationToken.ThrowIfCancellationRequested();

        if (timeout == TimeSpan.Zero)
        {
            if (!Monitor.TryEnter(gate, millisecondsTimeout: 0))
                return false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                Monitor.Exit(gate);
                throw;
            }

            return true;
        }

        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        long timeoutTicks = infinite ? 0 : GetStopwatchTimeoutTicks(timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!infinite && Stopwatch.GetTimestamp() - start >= timeoutTicks)
                return false;

            if (!Monitor.TryEnter(gate, CancellationPollingIntervalMilliseconds))
                continue;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                Monitor.Exit(gate);
                throw;
            }

            if (!infinite && Stopwatch.GetTimestamp() - start >= timeoutTicks)
            {
                Monitor.Exit(gate);
                return false;
            }

            return true;
        }
    }

    internal static bool TryEnter(System.Threading.Lock gate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (gate is null)
            throw new ArgumentNullException(nameof(gate));
        ValidateTimeout(timeout);

        cancellationToken.ThrowIfCancellationRequested();

        if (timeout == TimeSpan.Zero)
        {
            if (!gate.TryEnter(millisecondsTimeout: 0))
                return false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                gate.Exit();
                throw;
            }

            return true;
        }

        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        long timeoutTicks = infinite ? 0 : GetStopwatchTimeoutTicks(timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!infinite && Stopwatch.GetTimestamp() - start >= timeoutTicks)
                return false;

            if (!gate.TryEnter(CancellationPollingIntervalMilliseconds))
                continue;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                gate.Exit();
                throw;
            }

            if (!infinite && Stopwatch.GetTimestamp() - start >= timeoutTicks)
            {
                gate.Exit();
                return false;
            }

            return true;
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static long GetStopwatchTimeoutTicks(TimeSpan timeout)
    {
        // Use integer arithmetic with a ceiling so tiny positive timeouts do not round to zero.
        // UInt128 prevents overflow when TimeSpan's large finite range is scaled by the
        // platform-specific Stopwatch frequency.
        UInt128 scaledTicks = (UInt128)(ulong)timeout.Ticks * (ulong)Stopwatch.Frequency;
        UInt128 stopwatchTicks =
            (scaledTicks + (ulong)TimeSpan.TicksPerSecond - 1) / (ulong)TimeSpan.TicksPerSecond;

        return stopwatchTicks >= (UInt128)long.MaxValue
            ? long.MaxValue
            : (long)stopwatchTicks;
    }
}
