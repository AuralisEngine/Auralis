namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Owns one successful MakeCurrent/ReleaseCurrent pair. Acquire does not return a lease when
/// MakeCurrent throws, so Dispose never releases a context that was not successfully acquired.
/// </summary>
internal sealed class OpenGLContextLease : IDisposable
{
    private readonly IOpenGLContextProvider _provider;
    private int _disposed;

    private OpenGLContextLease(IOpenGLContextProvider provider) => _provider = provider;

    internal static OpenGLContextLease Acquire(IOpenGLContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Allocate the lease before changing thread context state. If MakeCurrent fails,
        // no lease escapes and ReleaseCurrent is deliberately not called.
        OpenGLContextLease lease = new(provider);
        provider.MakeCurrent();
        return lease;
    }

    /// <summary>Releases the provider context from the calling thread exactly once.</summary>
    /// <remarks>Must run on the thread that acquired the lease; it never destroys the native context.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _provider.ReleaseCurrent();
    }
}
