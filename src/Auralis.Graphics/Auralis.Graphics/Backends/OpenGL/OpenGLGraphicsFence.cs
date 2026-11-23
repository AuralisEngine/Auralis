using System.Diagnostics;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>Implements a monotonic timeline fence using ordered desktop OpenGL sync objects.</summary>
/// <remarks>
/// GPU signals insert GL sync-object completion points in command-stream order;
/// CPU signals advance the value immediately after validating that they do not overtake pending
/// GPU signals. Polling and CPU waits acquire the device context gate because GL sync queries
/// require this device's context to be current. Fence completion does not itself retire a submitted
/// command list; queue polling performs that separate lifecycle transition and releases retained resources.
/// </remarks>
internal sealed class OpenGLGraphicsFence : OpenGLNativeDeviceObject, IGraphicsFence
{
    private readonly OpenGLGraphicsDevice _device;
    private readonly ArbSync _syncApi;
    private readonly object _gate = new();
    private readonly Queue<SyncPoint> _syncs = new(16);
    private ulong _completedValue;
    private ulong _lastSignaledValue;

    internal OpenGLGraphicsFence(
        OpenGLGraphicsDevice device,
        ArbSync syncApi,
        ulong initialValue) : base(device)
    {
        _device = device;
        _syncApi = syncApi;
        _completedValue = initialValue;
        _lastSignaledValue = initialValue;
    }

    /// <summary>Gets the greatest timeline value whose completion has been observed.</summary>
    /// <remarks>Reading the value may block while acquiring the device context gate and polls pending GL sync objects.</remarks>
    /// <exception cref="ObjectDisposedException">The fence or its device has been disposed.</exception>
    public ulong CompletedValue
    {
        get
        {
            ThrowIfDisposed();
            using OpenGLContextScope context = _device.AcquireContext();
            lock (_gate)
            {
                PollCompleted(context.Gl, 0);
                return _completedValue;
            }
        }
    }

    /// <summary>Signals the timeline from the CPU. GPU submissions use SignalFromGpu instead.</summary>
    public void Signal(ulong value)
    {
        ThrowIfDisposed();
        using OpenGLContextScope context = _device.AcquireContext();
        lock (_gate)
        {
            EnsureMonotonic(value);
            PollCompleted(context.Gl, 0);
            GraphicsCommandValidation.ValidateCpuFenceSignalDoesNotOvertakeGpu(
                value,
                _syncs.TryPeek(out SyncPoint pending) ? pending.Value : null);
            _lastSignaledValue = value;
            _completedValue = value;
            DeleteCompletedSyncs(context.Gl, value);
            Monitor.PulseAll(_gate);
        }
    }

    internal void SignalFromGpu(GL gl, ulong value)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            EnsureMonotonic(value);
            nint sync = _syncApi.FenceSync(
                SyncCondition.SyncGpuCommandsComplete,
                SyncBehaviorFlags.None);
            if (sync == 0)
                throw new GraphicsException("OpenGL failed to create a GPU fence sync object.");

            _syncs.Enqueue(new SyncPoint(value, sync));
            _lastSignaledValue = value;
            Monitor.PulseAll(_gate);
        }
    }

    internal void EnqueueGpuWait(GL gl, ulong value)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            PollCompleted(gl, 0);
            if (value <= _completedValue)
                return;

            foreach (SyncPoint point in _syncs)
            {
                if (point.Value >= value)
                {
                    _syncApi.WaitSync(point.Handle, SyncBehaviorFlags.None, ulong.MaxValue);
                    return;
                }
            }
        }

        throw new GraphicsValidationException(
            $"Fence value {value} has not been submitted or CPU-signaled; OpenGL cannot enqueue a wait for an unknown future sync object.");
    }

    /// <summary>Waits on the CPU until the timeline reaches the requested value or the wait expires.</summary>
    /// <param name="value">Timeline value to observe.</param>
    /// <param name="timeout">Maximum elapsed wait, or <see cref="Timeout.InfiniteTimeSpan" /> for an unbounded wait.</param>
    /// <param name="cancellationToken">Token that can interrupt the wait.</param>
    /// <returns><see langword="true" /> when the value is complete; <see langword="false" /> when the timeout expires or the context gate cannot be acquired before the remaining deadline.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and is not infinite.</exception>
    /// <exception cref="OperationCanceledException">The wait is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The fence or its device has been disposed.</exception>
    /// <exception cref="GraphicsException">OpenGL reports a failed sync wait.</exception>
    public bool Wait(ulong value, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        long start = Stopwatch.GetTimestamp();
        long timeoutTicks = timeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                if (_completedValue >= value)
                    return true;

            long elapsedTicks = Stopwatch.GetTimestamp() - start;
            long remainingTicks = timeoutTicks == long.MaxValue ? long.MaxValue : timeoutTicks - elapsedTicks;
            if (remainingTicks <= 0)
                return false;

            TimeSpan remaining = remainingTicks == long.MaxValue
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
            ulong waitNanoseconds = remainingTicks == long.MaxValue || remainingTicks >= Stopwatch.Frequency / 1000
                ? 1_000_000UL
                : (ulong)Math.Max(0, remainingTicks * 1_000_000_000L / Stopwatch.Frequency);

            if (!_device.TryAcquireContext(remaining, cancellationToken, out OpenGLContextScope context))
                return false;

            bool completed;
            using (context)
            {
                lock (_gate)
                    completed = PollCompleted(context.Gl, waitNanoseconds) >= value;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (completed)
                return timeoutTicks == long.MaxValue ||
                    Stopwatch.GetTimestamp() - start <= timeoutTicks;

            if (timeoutTicks != long.MaxValue && Stopwatch.GetTimestamp() - start >= timeoutTicks)
                return false;

            lock (_gate)
            {
                if (_completedValue < value && _syncs.Count == 0)
                    Monitor.Wait(_gate, TimeSpan.FromMilliseconds(1));
            }
        }
    }
    private ulong PollCompleted(GL gl, ulong waitNanoseconds)
    {
        if (_syncs.Count == 0)
            return _completedValue;

        ulong completed = _completedValue;
        foreach (SyncPoint point in _syncs)
        {
            ARB status = _syncApi.ClientWaitSync(
                point.Handle,
                SyncObjectMask.Bit,
                waitNanoseconds);

            if (status == ARB.AlreadySignaled || status == ARB.ConditionSatisfied)
            {
                completed = point.Value;
                continue;
            }

            if (status == ARB.TimeoutExpired)
                break;

            if (status == ARB.WaitFailed)
                throw new GraphicsException("OpenGL ClientWaitSync failed while waiting for a fence.");
        }

        if (completed > _completedValue)
        {
            _completedValue = completed;
            DeleteCompletedSyncs(gl, completed);
            Monitor.PulseAll(_gate);
        }

        return _completedValue;
    }

    private void DeleteCompletedSyncs(GL gl, ulong value)
    {
        while (_syncs.TryPeek(out SyncPoint point) && point.Value <= value)
        {
            _syncApi.DeleteSync(point.Handle);
            _syncs.Dequeue();
        }
    }

    private void EnsureMonotonic(ulong value)
    {
        if (value <= _lastSignaledValue)
            throw new GraphicsValidationException(
                $"Fence values must increase monotonically. Last signal: {_lastSignaledValue}; requested: {value}.");
    }

    /// <inheritdoc />
    protected override void DisposeCore() => ReleaseOwnerReference();

    /// <inheritdoc />
    public override void ReleaseNative(GL gl)
    {
        lock (_gate)
        {
            while (_syncs.TryDequeue(out SyncPoint point))
                _syncApi.DeleteSync(point.Handle);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Associates a monotonic fence value with the GL sync handle inserted after its GPU work.</summary>
    private readonly record struct SyncPoint(ulong Value, nint Handle);
}
