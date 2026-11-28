using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Devices;
using Auralis.Graphics.Direct3D12.Runtime;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12AdapterEnumerationIntegrationTests
{
    [Fact]
    public void WindowsEnumerationReturnsUniqueD3D12CapableAdapterIdentities()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return;

        D3D12RuntimeStatus runtime = D3D12RuntimeAvailability.Probe();
        if (!runtime.IsAvailable)
            return;

        ReadOnlyMemory<GraphicsAdapterInfo> adapters =
            D3D12AdapterEnumerator.Enumerate(CancellationToken.None);

        ReadOnlySpan<GraphicsAdapterInfo> span = adapters.Span;
        for (int i = 0; i < span.Length; i++)
        {
            Assert.False(span[i].Id.IsDefault);
            Assert.Equal(
                D3D12CapabilityMapper.RequiredBaselineFeatures,
                span[i].SupportedFeatures.Enabled &
                D3D12CapabilityMapper.RequiredBaselineFeatures);

            for (int previous = 0; previous < i; previous++)
                Assert.NotEqual(span[previous].Id, span[i].Id);
        }
    }
}
