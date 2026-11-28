using Auralis.Graphics.Direct3D12.Devices;
using Auralis.Graphics.Direct3D12.Runtime;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12AdapterEnumerationIntegrationTests
{
    [Fact]
    public void WindowsEnumerationReturnsOnlyUniqueD3D12CapableAdapterIdentities()
    {
        if (!OperatingSystem.IsWindows())
            return;

        D3D12RuntimeStatus runtime = D3D12RuntimeAvailability.Probe();
        if (!runtime.IsAvailable)
            return;

        ReadOnlyMemory<Auralis.Graphics.Abstractions.GraphicsAdapterInfo> adapters =
            D3D12AdapterEnumerator.Enumerate(CancellationToken.None);

        ReadOnlySpan<Auralis.Graphics.Abstractions.GraphicsAdapterInfo> span = adapters.Span;
        for (int i = 0; i < span.Length; i++)
        {
            Assert.False(span[i].Id.IsDefault);
            Assert.Equal(D3D12AdapterInfoMapper.CoreSupportedFeatures, span[i].SupportedFeatures.Enabled);
            for (int previous = 0; previous < i; previous++)
                Assert.NotEqual(span[previous].Id, span[i].Id);
        }
    }
}
