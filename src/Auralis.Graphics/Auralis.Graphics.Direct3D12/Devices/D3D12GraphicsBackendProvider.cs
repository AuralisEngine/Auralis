using System.Runtime.Versioning;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Direct3D12.Interop;
using Auralis.Graphics.Direct3D12.Runtime;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>
/// Direct3D 12 backend provider. It discovers DXGI adapters, negotiates native capabilities,
/// and creates an owned D3D12 device without creating a window or presentation surface.
/// </summary>
public sealed unsafe class D3D12GraphicsBackendProvider : IGraphicsBackendProvider
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
    public bool IsAvailable => _runtimeStatus.IsAvailable;
    public string? UnavailableReason => _runtimeStatus.UnavailableReason;

    public ValueTask<ReadOnlyMemory<GraphicsAdapterInfo>> EnumerateAdaptersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
            throw new GraphicsBackendUnavailableException(Backend, UnavailableReason);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            throw new GraphicsBackendUnavailableException(
                Backend,
                "The Direct3D 12 backend requires Windows 10 version 1507 (build 10240) or later.");
        }

        return ValueTask.FromResult(D3D12AdapterEnumerator.Enumerate(cancellationToken));
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

        if (options.Backend is { } requestedBackend && requestedBackend != Backend)
            throw new ArgumentException(
                $"The {Backend} provider cannot create a device for {requestedBackend}.",
                nameof(options));

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            throw new GraphicsBackendUnavailableException(
                Backend,
                "The Direct3D 12 backend requires Windows 10 version 1507 (build 10240) or later.");
        }

        // Debug facilities must be configured before creating this provider's native device.
        D3D12DebugLayer.Configure(options.DebugOptions);
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlyMemory<GraphicsAdapterInfo> adapterMemory =
            D3D12AdapterEnumerator.Enumerate(cancellationToken);
        GraphicsAdapterInfo selectedAdapter = SelectAdapter(adapterMemory.Span, options.Adapter);

        IDXGIAdapter1* adapter = D3D12AdapterEnumerator.AcquireAdapter(
            selectedAdapter.Id,
            cancellationToken);
        ID3D12Device* device = null;

        try
        {
            Guid deviceIid = IID.IID_ID3D12Device;
            int createResult = D3D12CreateDevice(
                (IUnknown*)adapter,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                &deviceIid,
                (void**)&device);

            D3D12HResult.ThrowIfFailed(createResult, "D3D12CreateDevice");
            if (device == null)
                throw new GraphicsException("D3D12CreateDevice succeeded without returning ID3D12Device.");

            cancellationToken.ThrowIfCancellationRequested();

            D3D12FeatureSnapshot features = D3D12DeviceCapabilityQuery.QueryFeatures(device);
            GraphicsDeviceCapabilities capabilities =
                D3D12DeviceCapabilityQuery.QueryCapabilities(device, features);

            GraphicsFeature missingFeatures =
                features.SupportedFeatures.MissingFrom(options.RequiredFeatures);
            if (missingFeatures != GraphicsFeature.None)
            {
                throw new GraphicsFeatureNotSupportedException(
                    options.RequiredFeatures,
                    missingFeatures);
            }

            GraphicsQueueCapabilities missingQueues =
                options.RequiredQueues & ~capabilities.QueueCapabilities;
            if (missingQueues != GraphicsQueueCapabilities.None)
            {
                throw new GraphicsQueueCapabilitiesNotSupportedException(
                    options.RequiredQueues,
                    missingQueues);
            }

            GraphicsDescriptorIndexingCapabilities missingDescriptorIndexing =
                capabilities.DescriptorIndexing.MissingFrom(options.RequiredDescriptorIndexing);
            if (missingDescriptorIndexing.HasAny)
            {
                throw new GraphicsDescriptorIndexingNotSupportedException(
                    options.RequiredDescriptorIndexing,
                    missingDescriptorIndexing);
            }

            GraphicsAdapterInfo logicalAdapter = selectedAdapter with
            {
                SupportedFeatures = features.SupportedFeatures,
                SupportedDescriptorIndexing = features.SupportedDescriptorIndexing
            };

            string featureLevel = D3D12FeatureSnapshot.FormatFeatureLevel(features.HighestFeatureLevel);
            GraphicsDeviceInfo info = new(
                Name: selectedAdapter.Name,
                DriverName: "Unknown",
                DriverVersion: "Unknown",
                ApiVersion: $"Direct3D 12 (feature level {featureLevel})",
                VendorId: selectedAdapter.VendorId,
                DeviceId: selectedAdapter.DeviceId,
                IsSoftware: selectedAdapter.Type == GraphicsAdapterType.Cpu);

            D3D12GraphicsDevice graphicsDevice = new(
                device,
                logicalAdapter,
                info,
                capabilities);

            device = null; // Ownership transferred to D3D12GraphicsDevice.
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IGraphicsDevice>(graphicsDevice);
        }
        catch
        {
            if (device != null)
                device->Release();

            throw;
        }
        finally
        {
            adapter->Release();
        }
    }

    private static GraphicsAdapterInfo SelectAdapter(
        ReadOnlySpan<GraphicsAdapterInfo> adapters,
        GraphicsAdapterId? requestedAdapter)
    {
        if (requestedAdapter is { } requested)
        {
            for (int i = 0; i < adapters.Length; i++)
            {
                if (adapters[i].Id == requested)
                    return adapters[i];
            }

            throw new GraphicsBackendUnavailableException(
                GraphicsBackend.Direct3D12,
                $"The requested adapter ID 0x{requested.Low:X16} is not present in the current DXGI enumeration.");
        }

        // Prefer a local hardware adapter. WARP and other software adapters are a fallback only.
        for (int i = 0; i < adapters.Length; i++)
        {
            if (adapters[i].Type != GraphicsAdapterType.Cpu &&
                adapters[i].Type != GraphicsAdapterType.Virtual)
            {
                return adapters[i];
            }
        }

        for (int i = 0; i < adapters.Length; i++)
        {
            if (adapters[i].Type != GraphicsAdapterType.Cpu)
                return adapters[i];
        }

        if (!adapters.IsEmpty)
            return adapters[0];

        throw new GraphicsBackendUnavailableException(
            GraphicsBackend.Direct3D12,
            "DXGI did not report any adapter capable of creating a Direct3D 12 feature-level 11.0 device.");
    }
}
