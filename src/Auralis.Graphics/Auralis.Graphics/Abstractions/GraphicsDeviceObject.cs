using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Abstractions;

/// <summary>Common contract for objects whose lifetime is owned by a graphics device.</summary>
public interface IGraphicsDeviceObject : IDisposable
{
    IGraphicsDevice Device { get; }
    bool IsDisposed { get; }
}

/// <summary>
/// Base class for backend objects that need deterministic, idempotent disposal.
/// It deliberately has no finalizer: implementations must release native resources deterministically.
/// </summary>
public abstract class GraphicsDeviceObject : IGraphicsDeviceObject
{
    private int _disposeState;

    protected GraphicsDeviceObject(IGraphicsDevice device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public IGraphicsDevice Device { get; }

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    protected void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);

        if (Device.IsDisposed)
            throw new ObjectDisposedException(nameof(IGraphicsDevice));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        DisposeCore();
    }

    /// <summary>Releases this object's native or managed backend state. Called at most once.</summary>
    protected abstract void DisposeCore();
}
