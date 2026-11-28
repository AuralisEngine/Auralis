using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Direct3D12.Devices;
using Xunit;

namespace Auralis.Graphics.Direct3D12.Tests;

public sealed class D3D12CapabilityMapperTests
{
    [Fact]
    public void BaselineCapabilitiesContainOnlyGuaranteedD3D12Features()
    {
        D3D12FeatureSnapshot snapshot = CreateSnapshot();
        GraphicsFeature enabled = snapshot.SupportedFeatures.Enabled;

        Assert.Equal(
            D3D12CapabilityMapper.RequiredBaselineFeatures,
            enabled & D3D12CapabilityMapper.RequiredBaselineFeatures);

        Assert.True(enabled.HasFlag(GraphicsFeature.MultiDrawIndirect));
        Assert.True(enabled.HasFlag(GraphicsFeature.SeparateComputeQueue));
        Assert.True(enabled.HasFlag(GraphicsFeature.SeparateTransferQueue));
        Assert.True(enabled.HasFlag(GraphicsFeature.TimestampQueries));
        Assert.True(enabled.HasFlag(GraphicsFeature.SamplerAnisotropy));
        Assert.True(enabled.HasFlag(GraphicsFeature.MultiViewport));
        Assert.True(enabled.HasFlag(GraphicsFeature.FillModeNonSolid));

        Assert.False(enabled.HasFlag(GraphicsFeature.DeviceGroups));
        Assert.False(enabled.HasFlag(GraphicsFeature.Multiview));
        Assert.False(enabled.HasFlag(GraphicsFeature.DepthClamp));
        Assert.False(enabled.HasFlag(GraphicsFeature.WideLines));
    }

    [Fact]
    public void OptionalFeaturesAreAdvertisedOnlyForNonZeroNativeTiersOrBooleanSupport()
    {
        D3D12FeatureSnapshot snapshot = CreateSnapshot(
            resourceBindingTier: 2,
            tiledResourcesTier: 1,
            conservativeRasterizationTier: 1,
            rayTracingTier: 1,
            variableShadingRateTier: 2,
            meshShaderTier: 1,
            logicOperations: true,
            depthBoundsTest: true);

        GraphicsFeature enabled = snapshot.SupportedFeatures.Enabled;

        Assert.True(enabled.HasFlag(GraphicsFeature.DescriptorIndexing));
        Assert.True(enabled.HasFlag(GraphicsFeature.SparseResources));
        Assert.True(enabled.HasFlag(GraphicsFeature.ConservativeRasterization));
        Assert.True(enabled.HasFlag(GraphicsFeature.RayTracing));
        Assert.True(enabled.HasFlag(GraphicsFeature.VariableRateShading));
        Assert.True(enabled.HasFlag(GraphicsFeature.MeshShaders));
        Assert.True(enabled.HasFlag(GraphicsFeature.TaskShaders));
        Assert.True(enabled.HasFlag(GraphicsFeature.LogicOperations));
        Assert.True(enabled.HasFlag(GraphicsFeature.DepthBoundsTest));
    }

    [Fact]
    public void MissingOptionalQueriesDoNotAdvertiseOptionalFeatures()
    {
        GraphicsFeature enabled = CreateSnapshot().SupportedFeatures.Enabled;

        Assert.False(enabled.HasFlag(GraphicsFeature.DescriptorIndexing));
        Assert.False(enabled.HasFlag(GraphicsFeature.SparseResources));
        Assert.False(enabled.HasFlag(GraphicsFeature.ConservativeRasterization));
        Assert.False(enabled.HasFlag(GraphicsFeature.RayTracing));
        Assert.False(enabled.HasFlag(GraphicsFeature.VariableRateShading));
        Assert.False(enabled.HasFlag(GraphicsFeature.MeshShaders));
        Assert.False(enabled.HasFlag(GraphicsFeature.TaskShaders));
        Assert.False(enabled.HasFlag(GraphicsFeature.LogicOperations));
        Assert.False(enabled.HasFlag(GraphicsFeature.DepthBoundsTest));
    }

    [Fact]
    public void ResourceBindingTierTwoOnlyEnablesVariableDescriptorCountSemantics()
    {
        GraphicsDescriptorIndexingCapabilities tierOne =
            CreateSnapshot(resourceBindingTier: 1).SupportedDescriptorIndexing;
        GraphicsDescriptorIndexingCapabilities tierTwo =
            CreateSnapshot(resourceBindingTier: 2).SupportedDescriptorIndexing;

        Assert.False(tierOne.HasAny);
        Assert.True(tierTwo.VariableDescriptorCount);
        Assert.False(tierTwo.PartiallyBound);
        Assert.False(tierTwo.UpdateUnusedWhilePending);
        Assert.False(tierTwo.UniformBufferUpdateAfterBind);
        Assert.False(tierTwo.SampledImageUpdateAfterBind);
        Assert.False(tierTwo.StorageImageUpdateAfterBind);
        Assert.False(tierTwo.StorageBufferUpdateAfterBind);
        Assert.False(tierTwo.ZeroVariableDescriptorCount);
    }

    [Fact]
    public void BufferSizeLimitUsesGpuVirtualAddressWidthWithoutOverflow()
    {
        GraphicsDeviceLimits limits = D3D12CapabilityMapper.CreateLimits(
            CreateSnapshot(maxGpuVirtualAddressBits: 48));

        Assert.Equal(1UL << 48, limits.MaxBufferSizeInBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(255)]
    public void InvalidGpuVirtualAddressWidthIsReportedAsUnknown(uint bits)
    {
        GraphicsDeviceLimits limits = D3D12CapabilityMapper.CreateLimits(
            CreateSnapshot(maxGpuVirtualAddressBits: bits));

        Assert.Equal(0UL, limits.MaxBufferSizeInBytes);
    }

    private static D3D12FeatureSnapshot CreateSnapshot(
        uint resourceBindingTier = 0,
        uint tiledResourcesTier = 0,
        uint conservativeRasterizationTier = 0,
        uint rayTracingTier = 0,
        uint variableShadingRateTier = 0,
        uint meshShaderTier = 0,
        bool logicOperations = false,
        bool depthBoundsTest = false,
        uint maxGpuVirtualAddressBits = 48) =>
        new(
            HighestFeatureLevel: 0xC100,
            ResourceBindingTier: resourceBindingTier,
            TiledResourcesTier: tiledResourcesTier,
            ConservativeRasterizationTier: conservativeRasterizationTier,
            RayTracingTier: rayTracingTier,
            VariableShadingRateTier: variableShadingRateTier,
            MeshShaderTier: meshShaderTier,
            LogicOperations: logicOperations,
            DepthBoundsTest: depthBoundsTest,
            CopyQueueTimestampQueries: true,
            MaxGpuVirtualAddressBitsPerResource: maxGpuVirtualAddressBits,
            MaxShaderModel: 0x60,
            RootSignatureVersion: 0x10);
}
