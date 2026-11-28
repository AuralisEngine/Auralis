using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Devices;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12AdapterInfoMapperTests
{
    [Fact]
    public void AdapterIdentifierIsStableForSameLuid()
    {
        GraphicsAdapterId first = D3D12AdapterInfoMapper.CreateAdapterId(
            luidLowPart: 0x89ABCDEF,
            luidHighPart: unchecked((int)0x81234567));

        GraphicsAdapterId second = D3D12AdapterInfoMapper.CreateAdapterId(
            luidLowPart: 0x89ABCDEF,
            luidHighPart: unchecked((int)0x81234567));

        Assert.Equal(first, second);
        Assert.False(first.IsDefault);
        Assert.Equal(0x4433443132414450UL, first.High);
        Assert.Equal(0x8123456789ABCDEFUL, first.Low);
    }

    [Fact]
    public void DifferentLuidsProduceDifferentAdapterIdentifiers()
    {
        GraphicsAdapterId first = D3D12AdapterInfoMapper.CreateAdapterId(1, 0);
        GraphicsAdapterId second = D3D12AdapterInfoMapper.CreateAdapterId(2, 0);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ZeroLuidIsRejectedInsteadOfProducingDefaultIdentifier()
    {
        Assert.Throws<ArgumentException>(
            () => D3D12AdapterInfoMapper.CreateAdapterId(0, 0));
    }

    [Fact]
    public void SoftwareAdapterIsClassifiedAsCpu()
    {
        GraphicsAdapterInfo adapter = Create(isSoftware: true, isRemote: false);

        Assert.Equal(GraphicsAdapterType.Cpu, adapter.Type);
    }

    [Fact]
    public void RemoteHardwareAdapterIsClassifiedAsVirtual()
    {
        GraphicsAdapterInfo adapter = Create(isSoftware: false, isRemote: true);

        Assert.Equal(GraphicsAdapterType.Virtual, adapter.Type);
    }

    [Fact]
    public void HardwareAdapterWithoutReliableIntegratedDiscreteInformationRemainsUnknown()
    {
        GraphicsAdapterInfo adapter = Create(isSoftware: false, isRemote: false);

        Assert.Equal(GraphicsAdapterType.Unknown, adapter.Type);
    }

    [Fact]
    public void AdapterIdentityAndNativeMetadataArePreserved()
    {
        GraphicsAdapterInfo adapter = D3D12AdapterInfoMapper.Create(
            new D3D12AdapterDescriptor(
                "Auralis Test Adapter",
                VendorId: 0x10DE,
                DeviceId: 0x2684,
                DedicatedMemoryBytes: 12UL * 1024 * 1024 * 1024,
                LuidLowPart: 0x12345678,
                LuidHighPart: 0x10203040,
                IsSoftware: false,
                IsRemote: false));

        Assert.Equal("Auralis Test Adapter", adapter.Name);
        Assert.Equal(0x10DEu, adapter.VendorId);
        Assert.Equal(0x2684u, adapter.DeviceId);
        Assert.Equal(12UL * 1024 * 1024 * 1024, adapter.DedicatedMemoryBytes);
        Assert.Equal(
            D3D12AdapterInfoMapper.CreateAdapterId(0x12345678, 0x10203040),
            adapter.Id);
        Assert.Equal(GraphicsFeature.None, adapter.SupportedFeatures.Enabled);
        Assert.False(adapter.SupportedDescriptorIndexing.HasAny);
    }

    [Fact]
    public void EmptyNameUsesExplicitFallback()
    {
        GraphicsAdapterInfo adapter = Create(name: " ");

        Assert.Equal("Unknown Direct3D 12 adapter", adapter.Name);
    }

    private static GraphicsAdapterInfo Create(
        bool isSoftware = false,
        bool isRemote = false,
        string? name = "Test Adapter") =>
        D3D12AdapterInfoMapper.Create(
            new D3D12AdapterDescriptor(
                name,
                VendorId: 1,
                DeviceId: 2,
                DedicatedMemoryBytes: 1024,
                LuidLowPart: 1,
                LuidHighPart: 0,
                IsSoftware: isSoftware,
                IsRemote: isRemote));
}
