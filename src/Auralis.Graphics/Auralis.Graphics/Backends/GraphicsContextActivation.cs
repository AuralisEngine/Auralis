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
    internal static bool TryEnter(object gate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        long start = Stopwatch.GetTimestamp();
        long timeoutTicks = timeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long elapsed = Stopwatch.GetTimestamp() - start;
            long remaining = timeoutTicks == long.MaxValue ? long.MaxValue : timeoutTicks - elapsed;
            if (remaining <= 0)
                return false;

            int waitMilliseconds = remaining == long.MaxValue || remaining >= Stopwatch.Frequency / 1000
                ? 1
                : 0;
            if (!Monitor.TryEnter(gate, waitMilliseconds))
                continue;

            if (timeoutTicks != long.MaxValue &&
                Stopwatch.GetTimestamp() - start > timeoutTicks)
            {
                Monitor.Exit(gate);
                return false;
            }

            return true;
        }
    }
}
