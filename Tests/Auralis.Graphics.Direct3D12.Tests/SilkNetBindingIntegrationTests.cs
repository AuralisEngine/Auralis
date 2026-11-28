using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

/// <summary>
/// Compile-time integration guard for the generated COM interface types used by the backend.
/// These checks do not load the native runtime or create COM objects.
/// </summary>
public sealed class SilkNetBindingIntegrationTests
{
    [Fact]
    public void Direct3D12ComInterfaceIsAvailable()
    {
        Type bindingType = typeof(Silk.NET.Direct3D12.ID3D12Device);

        Assert.Equal("Silk.NET.Direct3D12", bindingType.Assembly.GetName().Name);
    }

    [Fact]
    public void DxgiFactoryInterfaceIsAvailable()
    {
        Type bindingType = typeof(Silk.NET.DXGI.IDXGIFactory4);

        Assert.Equal("Silk.NET.DXGI", bindingType.Assembly.GetName().Name);
    }
}
