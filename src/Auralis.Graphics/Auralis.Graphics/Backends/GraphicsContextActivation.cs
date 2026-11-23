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

    /// <summary>Creates a reentrant activation tracker for one native context.</summary>
    /// <param name="makeCurrent">Activates the host context on the calling thread.</param>
    /// <param name="releaseCurrent">Releases the context from the calling thread after the outermost activation.</param>
    /// <exception cref="ArgumentNullException">Either callback is <see langword="null" />.</exception>
    internal GraphicsContextActivation(Action makeCurrent, Action releaseCurrent)
    {
        _makeCurrent = makeCurrent ?? throw new ArgumentNullException(nameof(makeCurrent));
        _releaseCurrent = releaseCurrent ?? throw new ArgumentNullException(nameof(releaseCurrent));
    }

    /// <summary>Enters one activation level for the calling thread.</summary>
    /// <remarks>
    /// The caller must hold the device's context gate. The native make-current callback runs only
    /// when entering from depth zero; nested entries are accepted only from the owning thread.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The activation is already owned by another thread.</exception>
    /// <exception cref="OverflowException">The checked nesting depth cannot be incremented.</exception>
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

    /// <summary>Leaves one activation level and releases the native context at depth zero.</summary>
    /// <remarks>The caller must hold the same context gate used by <see cref="Enter"/>.</remarks>
    /// <exception cref="InvalidOperationException">No activation is active, or the calling thread does not own it.</exception>
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
/// <summary>Provides cancellation-aware, deadline-bounded acquisition of a device context gate.</summary>
/// <remarks>
/// The helper uses short timed acquisition attempts rather than an uninterruptible wait or a busy
/// spin. A successful return transfers one monitor/lock ownership to the caller, who must release
/// it exactly once on the acquiring thread. A timeout returns <see langword="false" /> without
/// retaining the gate; cancellation is reported by throwing <see cref="OperationCanceledException" />.
/// </remarks>
internal static class GraphicsContextGate
{
    // Short timed waits keep cancellation responsive without spinning while the context is busy.
    private const int CancellationPollingIntervalMilliseconds = 1;

    /// <summary>Attempts to enter an object monitor before the deadline.</summary>
    /// <param name="gate">The monitor object protecting context access.</param>
    /// <param name="timeout">Maximum wait duration, or <see cref="Timeout.InfiniteTimeSpan" /> for no deadline.</param>
    /// <param name="cancellationToken">Token that can cancel the acquisition while it is waiting.</param>
    /// <returns><see langword="true" /> when the caller owns the monitor; otherwise <see langword="false" /> when the deadline expires.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gate" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token is canceled before acquisition completes.</exception>
    internal static bool TryEnter(object gate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (gate is null)
            throw new ArgumentNullException(nameof(gate));
        ValidateTimeout(timeout);

        cancellationToken.ThrowIfCancellationRequested();

        if (timeout == TimeSpan.Zero)
        {
            if (!Monitor.TryEnter(gate, 0))
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

    /// <summary>Attempts to enter a <see cref="System.Threading.Lock" /> before the deadline.</summary>
    /// <param name="gate">The lock protecting context access.</param>
    /// <param name="timeout">Maximum wait duration, or <see cref="Timeout.InfiniteTimeSpan" /> for no deadline.</param>
    /// <param name="cancellationToken">Token that can cancel the acquisition while it is waiting.</param>
    /// <returns><see langword="true" /> when the caller owns the lock; otherwise <see langword="false" /> when the deadline expires.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gate" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token is canceled before acquisition completes.</exception>
    internal static bool TryEnter(System.Threading.Lock gate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (gate is null)
            throw new ArgumentNullException(nameof(gate));
        ValidateTimeout(timeout);

        cancellationToken.ThrowIfCancellationRequested();

        if (timeout == TimeSpan.Zero)
        {
            if (!gate.TryEnter(0))
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

    /// <summary>Rejects negative timeout values other than the infinite sentinel.</summary>
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
