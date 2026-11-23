using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Explicit provider for a host-created desktop OpenGL 4.5 Core Profile context.
/// No windowing or native context-creation package is required.
/// </summary>
public sealed class OpenGLGraphicsBackendProvider : IGraphicsBackendProvider
{
    private readonly IOpenGLContextProvider _contextProvider;

    /// <summary>Creates a backend provider around one host-selected desktop OpenGL context.</summary>
    /// <param name="contextProvider">Provider for context activation, entry-point resolution, and optional drawable presentation.</param>
    /// <exception cref="ArgumentNullException">The provider or its context identity is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The provider reports an undefined ownership value.</exception>
    public OpenGLGraphicsBackendProvider(IOpenGLContextProvider contextProvider)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        ArgumentNullException.ThrowIfNull(contextProvider.ContextIdentity);
        if (!Enum.IsDefined(contextProvider.Ownership))
            throw new ArgumentOutOfRangeException(nameof(contextProvider), "Context ownership is invalid.");
    }

    /// <inheritdoc />
    public GraphicsBackend Backend => GraphicsBackend.OpenGL;

    /// <inheritdoc />
    public bool IsAvailable => _contextProvider.IsAvailable;

    /// <inheritdoc />
    public string? UnavailableReason => IsAvailable
        ? null
        : _contextProvider.UnavailableReason ?? "The injected OpenGL context provider is unavailable.";

    /// <summary>Checks provider availability without fabricating system-wide adapter enumeration.</summary>
    /// <param name="cancellationToken">Token used to cancel the operation before it returns.</param>
    /// <returns>An empty adapter collection because this provider wraps one already-selected host context.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="GraphicsBackendUnavailableException">The supplied context provider is unavailable.</exception>
    public ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
            throw new GraphicsBackendUnavailableException(Backend, UnavailableReason);

        // A context provider supplies one already-selected context, not a system-wide adapter
        // enumeration facility. Return no fabricated physical-adapter entries.
        return ValueTask.FromResult<ReadOnlyMemory<GraphicsAdapterInfo>>(ReadOnlyMemory<GraphicsAdapterInfo>.Empty);
    }

    /// <summary>Creates the desktop OpenGL device over the injected currentable context.</summary>
    /// <param name="options">Device requirements; a non-default adapter selection and device-created debug-context options are unsupported.</param>
    /// <param name="cancellationToken">Token checked during device creation and capability discovery.</param>
    /// <returns>The initialized graphics device backed by the provider's context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="GraphicsBackendUnavailableException">The context is unavailable, the requested adapter cannot be selected, or the context does not meet the required OpenGL 4.5 Core Profile contract.</exception>
    /// <exception cref="NotSupportedException">Debug-context options were requested; such options must be applied by the host before context injection.</exception>
    /// <exception cref="OperationCanceledException">Creation was canceled.</exception>
    public ValueTask<IGraphicsDevice> CreateDeviceAsync(
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
            throw new GraphicsBackendUnavailableException(Backend, UnavailableReason);
        if (options.Adapter is { } requested && !requested.IsDefault)
            throw new GraphicsBackendUnavailableException(Backend,
                "The injected context provider cannot select among physical adapters.");
        if (options.DebugOptions != GraphicsDebugOptions.None)
            throw new NotSupportedException(
                "OpenGL debug-context options must be configured by the host before supplying the context.");

        IGraphicsDevice device = OpenGLGraphicsDevice.Create(_contextProvider, options, cancellationToken);
        return ValueTask.FromResult(device);
    }
}
