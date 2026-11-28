using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

/// <summary>
/// Compile-time integration guard: the backend project must expose generated types from
/// the Direct3D 12 and DXGI binding assemblies it references.
/// </summary>
public sealed class SilkNetBindingIntegrationTests
{
    [Fact]
    public void Direct3D12BindingAssemblyIsReferenced()
    {
        Type bindingType = typeof(Silk.NET.Direct3D12.D3D12);

        Assert.Equal("Silk.NET.Direct3D12", bindingType.Assembly.GetName().Name);
    }

    [Fact]
    public void DxgiBindingAssemblyIsReferenced()
    {
        Type bindingType = typeof(Silk.NET.DXGI.DXGI);

        Assert.Equal("Silk.NET.DXGI", bindingType.Assembly.GetName().Name);
    }
}
