using System.Diagnostics;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Synchronization;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>Timeline-style fence implemented with an ordered queue of GLES Sync objects.</summary>
internal sealed class OpenGLESGraphicsFence : OpenGLESNativeDeviceObject, IGraphicsFence
{
    private readonly OpenGLESGraphicsDevice _device;
    private readonly object _gate = new();
    private readonly Queue<SyncPoint> _syncs = new();
    private ulong _completedValue;
    private ulong _lastSignaledValue;

    internal OpenGLESGraphicsFence(OpenGLESGraphicsDevice device, ulong initialValue) : base(device)
    {
        _device = device;
        _completedValue = initialValue;
        _lastSignaledValue = initialValue;
    }

    public ulong CompletedValue
    {
        get
        {
            ThrowIfDisposed();
            using OpenGLESContextScope context = _device.AcquireContext();
            lock (_gate)
                return Poll(context.Gl, 0);
        }
    }

    public void Signal(ulong value)
    {
        ThrowIfDisposed();
        using OpenGLESContextScope context = _device.AcquireContext();
        lock (_gate)
        {
            EnsureMonotonic(value);
            Poll(context.Gl, 0);
            GraphicsCommandValidation.ValidateCpuFenceSignalDoesNotOvertakeGpu(
                value,
                _syncs.TryPeek(out SyncPoint pending) ? pending.Value : null);
            _lastSignaledValue = value;
            _completedValue = value;
            DeleteCompleted(context.Gl, value);
            Monitor.PulseAll(_gate);
        }
    }

    internal void SignalFromGpu(GL gl, ulong value)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            EnsureMonotonic(value);
            nint sync = gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, (uint)0);
            if (sync == 0)
                throw new GraphicsException("OpenGL ES failed to create a GPU sync object.");
            _syncs.Enqueue(new SyncPoint(value, sync));
            _lastSignaledValue = value;
        }
    }

    internal void EnqueueGpuWait(GL gl, ulong value)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            Poll(gl, 0);
            if (value <= _completedValue)
                return;

            foreach (SyncPoint point in _syncs)
            {
                if (point.Value >= value)
                {
                    gl.WaitSync(point.Handle, (uint)0, ulong.MaxValue);
                    return;
                }
            }
        }

        throw new GraphicsValidationException(
            $"Fence value {value} has not been signaled; an unknown future GLES sync cannot be waited on.");
    }

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

            if (!_device.TryAcquireContext(remaining, cancellationToken, out OpenGLESContextScope context))
                return false;

            bool completed;
            using (context)
            {
                lock (_gate)
                    completed = Poll(context.Gl, waitNanoseconds) >= value;
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
    private ulong Poll(GL gl, ulong waitNanoseconds)
    {
        ulong completed = _completedValue;
        while (_syncs.TryPeek(out SyncPoint point))
        {
            GLEnum status = gl.ClientWaitSync(point.Handle, (uint)0, waitNanoseconds);
            if (status == GLEnum.TimeoutExpired)
                break;
            if (status == GLEnum.WaitFailed)
                throw new GraphicsException("OpenGL ES ClientWaitSync failed.");
            if (status is not (GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied))
                throw new GraphicsException($"OpenGL ES returned unexpected sync status {status}.");

            gl.DeleteSync(point.Handle);
            _syncs.Dequeue();
            completed = point.Value;
        }

        if (completed > _completedValue)
        {
            _completedValue = completed;
            Monitor.PulseAll(_gate);
        }
        return _completedValue;
    }

    private void DeleteCompleted(GL gl, ulong value)
    {
        while (_syncs.TryPeek(out SyncPoint point) && point.Value <= value)
        {
            gl.DeleteSync(point.Handle);
            _syncs.Dequeue();
        }
    }

    private void EnsureMonotonic(ulong value)
    {
        if (value <= _lastSignaledValue)
            throw new GraphicsValidationException(
                $"Fence values must increase monotonically. Last signal: {_lastSignaledValue}; requested: {value}.");
    }

    protected override void DisposeCore() => ReleaseOwnerReference();

    public override void ReleaseNative(GL gl)
    {
        lock (_gate)
        {
            while (_syncs.TryDequeue(out SyncPoint point))
                gl.DeleteSync(point.Handle);
            Monitor.PulseAll(_gate);
        }
    }

    private readonly record struct SyncPoint(ulong Value, nint Handle);
}
