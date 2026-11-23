namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Owns one successful MakeCurrent/ReleaseCurrent pair. A failed activation never releases a context
/// that was not successfully made current.
/// </summary>
internal sealed class OpenGLESContextLease : IDisposable
{
    private readonly IOpenGLESContextProvider _provider;
    private int _disposed;

    private OpenGLESContextLease(IOpenGLESContextProvider provider) => _provider = provider;

    internal static OpenGLESContextLease Acquire(IOpenGLESContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        OpenGLESContextLease lease = new(provider);
        provider.MakeCurrent();
        return lease;
    }

    /// <summary>Releases the provider context from the calling thread exactly once.</summary>
    /// <remarks>Must run on the thread that acquired the lease; it does not destroy the native GLES context.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _provider.ReleaseCurrent();
    }
}
