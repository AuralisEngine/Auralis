using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Devices;
using Auralis.Graphics.Direct3D12.Runtime;
using Auralis.Graphics.Devices;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12GraphicsBackendProviderTests
{
    [Fact]
    public void ProviderIdentifiesTheDirect3D12Backend()
    {
        D3D12GraphicsBackendProvider provider = new(
            D3D12RuntimeAvailability.Evaluate(true, true, true));

        Assert.Equal(GraphicsBackend.Direct3D12, provider.Backend);
    }

    [Fact]
    public void ProviderAvailabilityReflectsBothNativeRuntimeLibraries()
    {
        D3D12GraphicsBackendProvider provider = new(
            D3D12RuntimeAvailability.Evaluate(true, true, false));

        Assert.False(provider.IsAvailable);
        Assert.Contains("d3d12.dll", provider.UnavailableReason!);
    }

    [Fact]
    public async Task AdapterEnumerationRejectsPreCancelledRequests()
    {
        D3D12GraphicsBackendProvider provider = new(
            D3D12RuntimeAvailability.Evaluate(true, true, true));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.EnumerateAdaptersAsync(cancellation.Token));
    }

    [Fact]
    public async Task AdapterEnumerationReportsUnavailableRuntime()
    {
        D3D12GraphicsBackendProvider provider = new(
            D3D12RuntimeAvailability.Evaluate(false, false, false));

        GraphicsBackendUnavailableException exception =
            await Assert.ThrowsAsync<GraphicsBackendUnavailableException>(async () =>
                await provider.EnumerateAdaptersAsync());

        Assert.Equal(GraphicsBackend.Direct3D12, exception.Backend);
        Assert.Contains("only on Windows", exception.Message);
    }

    [Fact]
    public async Task DeviceCreationDoesNotReturnPartiallyInitializedDevice()
    {
        D3D12GraphicsBackendProvider provider = new(
            D3D12RuntimeAvailability.Evaluate(true, true, true));

        GraphicsBackendUnavailableException exception =
            await Assert.ThrowsAsync<GraphicsBackendUnavailableException>(async () =>
                await provider.CreateDeviceAsync(
                    new GraphicsDeviceOptions { Backend = GraphicsBackend.Direct3D12 }));

        Assert.Contains("D3D12-03", exception.Message);
    }
}
