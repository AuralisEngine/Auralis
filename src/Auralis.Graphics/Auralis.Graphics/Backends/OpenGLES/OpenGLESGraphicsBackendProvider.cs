using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// Provider for a host-created OpenGL ES 3.1+ context. It never creates windows, EGL contexts,
/// Android surfaces, or other platform objects.
/// </summary>
public sealed class OpenGLESGraphicsBackendProvider : IGraphicsBackendProvider
{
    private readonly IOpenGLESContextProvider _contextProvider;

    /// <summary>Creates a backend provider around one host-selected OpenGL ES context.</summary>
    /// <param name="contextProvider">Provider for GLES context activation, native entry-point resolution, and host presentation.</param>
    /// <exception cref="ArgumentNullException">The provider or its context identity is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The provider reports an undefined ownership or state-ownership value.</exception>
    public OpenGLESGraphicsBackendProvider(IOpenGLESContextProvider contextProvider)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        ArgumentNullException.ThrowIfNull(contextProvider.ContextIdentity);
        if (!Enum.IsDefined(contextProvider.Ownership))
            throw new ArgumentOutOfRangeException(nameof(contextProvider), "Context ownership is invalid.");
        if (!Enum.IsDefined(contextProvider.StateOwnership))
            throw new ArgumentOutOfRangeException(nameof(contextProvider), "Context state ownership is invalid.");
    }

    /// <inheritdoc />
    public GraphicsBackend Backend => GraphicsBackend.OpenGLES;

    /// <inheritdoc />
    public bool IsAvailable => _contextProvider.IsAvailable;

    /// <inheritdoc />
    public string? UnavailableReason => IsAvailable
        ? null
        : _contextProvider.UnavailableReason ?? "The injected OpenGL ES context provider is unavailable.";

    /// <summary>Exposes the host-selected GLES context as the single logical adapter.</summary>
    /// <param name="cancellationToken">Token used to cancel the operation before enumeration completes.</param>
    /// <returns>A one-entry adapter collection; the provider cannot enumerate or select physical devices.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="GraphicsBackendUnavailableException">The injected context provider is unavailable.</exception>
    public ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();

        // The host-provided context already selects the adapter. Expose one default logical adapter
        // so GraphicsDeviceFactory can follow its normal enumeration/selection path without inventing
        // a platform-specific adapter identifier.
        GraphicsAdapterInfo[] adapters =
        [
            new GraphicsAdapterInfo(
                default,
                "OpenGL ES host context",
                GraphicsAdapterType.Unknown,
                0,
                0,
                0)
        ];
        return ValueTask.FromResult<ReadOnlyMemory<GraphicsAdapterInfo>>(adapters);
    }

    /// <summary>Creates an OpenGL ES 3.1-or-newer device over the injected host context.</summary>
    /// <param name="options">Device and queue requirements; adapter selection must remain default and the selected backend must be OpenGL ES.</param>
    /// <param name="cancellationToken">Token checked before and during device initialization.</param>
    /// <returns>The initialized GLES graphics device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The options select a different graphics backend.</exception>
    /// <exception cref="GraphicsBackendUnavailableException">The provider is unavailable, a non-default physical adapter was requested, or the context does not satisfy the GLES baseline.</exception>
    /// <exception cref="NotSupportedException">Shared host state ownership or device-created debug-context options were requested.</exception>
    /// <exception cref="AggregateException">Initialization failed and disposal of an owned context provider also failed.</exception>
    /// <exception cref="OperationCanceledException">Creation was canceled.</exception>
    public ValueTask<IGraphicsDevice> CreateDeviceAsync(
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();

        if (options.Backend is { } backend && backend != GraphicsBackend.OpenGLES)
            throw new ArgumentException("The options select a backend other than OpenGL ES.", nameof(options));

        if (options.Adapter is { } requested && !requested.IsDefault)
            throw new GraphicsBackendUnavailableException(
                Backend, "An externally supplied GLES context cannot select among physical adapters.");

        if (_contextProvider.StateOwnership != OpenGLESContextStateOwnership.Exclusive)
            throw new NotSupportedException(
                "OpenGL ES currently requires exclusive context-state ownership; host GLES calls cannot be interleaved.");

        if (options.DebugOptions != GraphicsDebugOptions.None)
            throw new NotSupportedException(
                "Debug-context creation options must be configured by the host before supplying the GLES context.");

        try
        {
            IGraphicsDevice device = OpenGLESGraphicsDevice.Create(_contextProvider, options, cancellationToken);
            return ValueTask.FromResult(device);
        }
        catch (Exception creationError)
        {
            if (_contextProvider.Ownership == OpenGLESContextOwnership.Owned)
            {
                try
                {
                    _contextProvider.Dispose();
                }
                catch (Exception disposalError)
                {
                    throw new AggregateException(
                        "OpenGL ES device initialization failed and the owned context provider could not be disposed.",
                        creationError,
                        disposalError);
                }
            }

            throw;
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new GraphicsBackendUnavailableException(Backend, UnavailableReason);
    }
}
