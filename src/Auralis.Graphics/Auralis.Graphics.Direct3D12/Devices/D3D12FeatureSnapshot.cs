using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Direct3D12.Devices;

/// <summary>Immutable results of native D3D12 feature queries for one physical adapter/device.</summary>
internal readonly record struct D3D12FeatureSnapshot(
    uint HighestFeatureLevel,
    uint ResourceBindingTier,
    uint TiledResourcesTier,
    uint ConservativeRasterizationTier,
    uint RayTracingTier,
    uint VariableShadingRateTier,
    uint MeshShaderTier,
    bool LogicOperations,
    bool DepthBoundsTest,
    bool CopyQueueTimestampQueries,
    uint MaxGpuVirtualAddressBitsPerResource,
    uint MaxShaderModel,
    uint RootSignatureVersion)
{
    internal GraphicsFeatureSet SupportedFeatures => D3D12CapabilityMapper.MapFeatures(this);

    internal GraphicsDescriptorIndexingCapabilities SupportedDescriptorIndexing =>
        D3D12CapabilityMapper.MapDescriptorIndexing(this);

    internal static string FormatFeatureLevel(uint value) => value switch
    {
        0xB000 => "11.0",
        0xB100 => "11.1",
        0xC000 => "12.0",
        0xC100 => "12.1",
        0xC200 => "12.2",
        _ => "11.0"
    };
}

internal static class D3D12CapabilityMapper
{
    internal const GraphicsQueueCapabilities QueueCapabilities =
        GraphicsQueueCapabilities.Graphics |
        GraphicsQueueCapabilities.Compute |
        GraphicsQueueCapabilities.Copy |
        GraphicsQueueCapabilities.Present;

    internal const GraphicsFeature RequiredBaselineFeatures =
        GraphicsFeature.ComputeShaders |
        GraphicsFeature.IndirectDrawing |
        GraphicsFeature.TessellationShaders |
        GraphicsFeature.GeometryShaders |
        GraphicsFeature.TimelineSynchronization;

    public static GraphicsFeatureSet MapFeatures(in D3D12FeatureSnapshot snapshot)
    {
        GraphicsFeature features =
            RequiredBaselineFeatures |
            GraphicsFeature.MultiDrawIndirect |
            GraphicsFeature.SeparateComputeQueue |
            GraphicsFeature.SeparateTransferQueue |
            GraphicsFeature.TimestampQueries |
            GraphicsFeature.SamplerAnisotropy |
            GraphicsFeature.MultiViewport |
            GraphicsFeature.FillModeNonSolid;

        if (snapshot.ResourceBindingTier >= 2)
            features |= GraphicsFeature.DescriptorIndexing;

        if (snapshot.LogicOperations)
            features |= GraphicsFeature.LogicOperations;

        if (snapshot.DepthBoundsTest)
            features |= GraphicsFeature.DepthBoundsTest;

        if (snapshot.ConservativeRasterizationTier != 0)
            features |= GraphicsFeature.ConservativeRasterization;

        if (snapshot.TiledResourcesTier != 0)
            features |= GraphicsFeature.SparseResources;

        if (snapshot.RayTracingTier != 0)
            features |= GraphicsFeature.RayTracing;

        if (snapshot.VariableShadingRateTier != 0)
            features |= GraphicsFeature.VariableRateShading;

        if (snapshot.MeshShaderTier != 0)
            features |= GraphicsFeature.MeshShaders | GraphicsFeature.TaskShaders;

        return new GraphicsFeatureSet(features);
    }

    public static GraphicsDescriptorIndexingCapabilities MapDescriptorIndexing(
        in D3D12FeatureSnapshot snapshot)
    {
        // D3D12 Resource Binding Tier 2+ provides unbounded descriptor ranges. It does not
        // provide the common update-after-bind/partially-bound semantics. Descriptor memory
        // is retained and cannot be rewritten while submitted GPU work may read it.
        bool variableCount = snapshot.ResourceBindingTier >= 2;
        return new GraphicsDescriptorIndexingCapabilities(
            PartiallyBound: false,
            UpdateUnusedWhilePending: false,
            VariableDescriptorCount: variableCount,
            UniformBufferUpdateAfterBind: false,
            SampledImageUpdateAfterBind: false,
            StorageImageUpdateAfterBind: false,
            StorageBufferUpdateAfterBind: false,
            ZeroVariableDescriptorCount: false);
    }

    public static GraphicsDeviceLimits CreateLimits(in D3D12FeatureSnapshot snapshot)
    {
        ulong maxBufferSize = snapshot.MaxGpuVirtualAddressBitsPerResource is > 0 and < 64
            ? 1UL << (int)snapshot.MaxGpuVirtualAddressBitsPerResource
            : 0;

        return new GraphicsDeviceLimits(
            MaxBufferSizeInBytes: maxBufferSize,
            MaxTextureDimension1D: 16_384,
            MaxTextureDimension2D: 16_384,
            MaxTextureDimension3D: 2_048,
            MaxTextureArrayLayers: 2_048,
            MaxVertexInputAttributes: 32,
            MaxVertexInputBindings: 32,
            MaxBoundDescriptorSets: 64,
            MaxComputeWorkGroupInvocations: 1_024,
            MaxComputeWorkGroupSizeX: 1_024,
            MaxComputeWorkGroupSizeY: 1_024,
            MaxComputeWorkGroupSizeZ: 64,
            MaxSamplerAnisotropy: 16,
            MaxTessellationPatchSize: 32,
            MinUniformBufferOffsetAlignment: 256,
            MinStorageBufferOffsetAlignment: 4,
            MaxCubeTextureDimension: 16_384,
            MaxColorAttachments: 8,
            MaxComputeWorkGroupCountX: 65_535,
            MaxComputeWorkGroupCountY: 65_535,
            MaxComputeWorkGroupCountZ: 65_535);
    }
}
