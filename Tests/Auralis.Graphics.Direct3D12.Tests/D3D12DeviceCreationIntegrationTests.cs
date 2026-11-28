using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Direct3D12.Devices;
using Auralis.Graphics.Direct3D12.Runtime;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12DeviceCreationIntegrationTests
{
    [Fact]
    public async Task WindowsProviderCreatesAndDisposesARealD3D12Device()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return;

        D3D12RuntimeStatus runtime = D3D12RuntimeAvailability.Probe();
        if (!runtime.IsAvailable)
            return;

        D3D12GraphicsBackendProvider provider = new();
        ReadOnlyMemory<GraphicsAdapterInfo> adapters = await provider.EnumerateAdaptersAsync();
        Assert.NotEmpty(adapters.ToArray());

        GraphicsAdapterInfo selectedAdapter = adapters.Span[0];
        IGraphicsDevice device = await provider.CreateDeviceAsync(
            new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.Direct3D12,
                Adapter = selectedAdapter.Id,
                RequiredQueues = GraphicsQueueCapabilities.Graphics
            });

        Assert.IsType<D3D12GraphicsDevice>(device);
        Assert.Equal(GraphicsBackend.Direct3D12, device.Backend);
        Assert.Equal(selectedAdapter.Id, device.Adapter.Id);
        Assert.Equal(GraphicsDeviceStatus.Operational, Assert.IsAssignableFrom<IGraphicsDeviceHealth>(device).Status);
        Assert.False(device.IsDisposed);

        IGraphicsDeviceHealth health = Assert.IsAssignableFrom<IGraphicsDeviceHealth>(device);
        int disposedTransitions = 0;
        health.StatusChanged += status =>
        {
            if (status == GraphicsDeviceStatus.Disposed)
                disposedTransitions++;
        };

        device.Dispose();

        Assert.True(device.IsDisposed);
        Assert.Equal(GraphicsDeviceStatus.Disposed, health.Status);
        Assert.Equal(1, disposedTransitions);
        Assert.Throws<ObjectDisposedException>(() => ((D3D12GraphicsDevice)device).ThrowIfUnavailable());

        device.Dispose();
        Assert.Equal(1, disposedTransitions);
    }

    [Fact]
    public async Task RequiredUnsupportedFeatureIsRejectedDuringCreation()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return;

        D3D12RuntimeStatus runtime = D3D12RuntimeAvailability.Probe();
        if (!runtime.IsAvailable)
            return;

        D3D12GraphicsBackendProvider provider = new();
        ReadOnlyMemory<GraphicsAdapterInfo> adapters = await provider.EnumerateAdaptersAsync();
        Assert.NotEmpty(adapters.ToArray());

        GraphicsFeatureNotSupportedException exception =
            await Assert.ThrowsAsync<GraphicsFeatureNotSupportedException>(async () =>
                await provider.CreateDeviceAsync(
                    new GraphicsDeviceOptions
                    {
                        Backend = GraphicsBackend.Direct3D12,
                        Adapter = adapters.Span[0].Id,
                        RequiredFeatures = GraphicsFeature.DeviceGroups
                    }));

        Assert.Equal(GraphicsFeature.DeviceGroups, exception.Missing);
    }
}
