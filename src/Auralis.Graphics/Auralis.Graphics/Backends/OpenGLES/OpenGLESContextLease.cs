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

    /// <summary>Makes the provider's existing GLES context current and returns a lease for its release.</summary>
    /// <param name="provider">Host-supplied provider for the GLES context.</param>
    /// <returns>A lease that releases the context once when disposed on the acquiring thread.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The provider cannot activate its context on this thread.</exception>
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
