using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Runtime;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>
/// Direct3D 12 backend provider. It reports Windows runtime availability and enumerates physical
/// DXGI adapters; logical-device construction is supplied by the next implementation phase.
/// </summary>
public sealed class D3D12GraphicsBackendProvider : IGraphicsBackendProvider
{
    private readonly D3D12RuntimeStatus _runtimeStatus;

    public D3D12GraphicsBackendProvider()
        : this(D3D12RuntimeAvailability.Probe())
    {
    }

    internal D3D12GraphicsBackendProvider(D3D12RuntimeStatus runtimeStatus)
    {
        _runtimeStatus = runtimeStatus;
    }

    public GraphicsBackend Backend => GraphicsBackend.Direct3D12;

    /// <summary>
    /// Indicates that the Windows DXGI and D3D12 system libraries are loadable.
    /// It does not imply that an adapter or a requested feature is available.
    /// </summary>
    public bool IsAvailable => _runtimeStatus.IsAvailable;

    public string? UnavailableReason => _runtimeStatus.UnavailableReason;

    public ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
            throw new GraphicsBackendUnavailableException(Backend, UnavailableReason);

        ReadOnlyMemory<GraphicsAdapterInfo> adapters =
            D3D12AdapterEnumerator.Enumerate(cancellationToken);

        return ValueTask.FromResult(adapters);
    }

    public ValueTask<IGraphicsDevice> CreateDeviceAsync(
        GraphicsDeviceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
            throw new GraphicsBackendUnavailableException(Backend, UnavailableReason);

        // Device creation is intentionally unavailable until D3D12-03 supplies the native device
        // and all mandatory IGraphicsDevice contracts. No partially initialized device is returned.
        throw new GraphicsBackendUnavailableException(
            Backend,
            "DXGI discovery is available, but logical Direct3D 12 device creation is not available until D3D12-03 is implemented.");
    }
}
