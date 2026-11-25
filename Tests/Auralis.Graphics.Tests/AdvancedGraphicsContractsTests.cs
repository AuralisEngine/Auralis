using Auralis.Graphics.Resources;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Abstractions;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class AdvancedGraphicsTests
{
    [Fact]
    public void MemoryAllocationDescription_AcceptsPowerOfTwoAlignment()
    {
        var description = new GraphicsMemoryAllocationDescription(
            SizeInBytes: 4096,
            Alignment: 256,
            MemoryTypeBits: 0b101,
            RequiredProperties: GraphicsMemoryProperty.DeviceLocal,
            PreferredProperties: GraphicsMemoryProperty.HostCached,
            Usage: GraphicsMemoryUsage.GpuOnly);

        description.Validate();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(192)]
    public void MemoryAllocationDescription_RejectsInvalidAlignment(ulong alignment)
    {
        var description = new GraphicsMemoryAllocationDescription(
            1024, alignment, 1, GraphicsMemoryProperty.DeviceLocal,
            GraphicsMemoryProperty.None, GraphicsMemoryUsage.GpuOnly);

        Assert.Throws<ArgumentOutOfRangeException>(description.Validate);
    }

    [Fact]
    public void MemoryAllocationDescription_RejectsEmptyMemoryTypeMask()
    {
        var description = new GraphicsMemoryAllocationDescription(
            1024, 0, 0, GraphicsMemoryProperty.DeviceLocal,
            GraphicsMemoryProperty.None, GraphicsMemoryUsage.GpuOnly);

        Assert.Throws<ArgumentOutOfRangeException>(description.Validate);
    }

    [Fact]
    public void QueryPoolDescription_RequiresStatisticsForStatisticsQueries()
    {
        var description = new GraphicsQueryPoolDescription(
            GraphicsQueryType.PipelineStatistics, 4,
            GraphicsPipelineStatistics.VertexShaderInvocations);

        description.Validate();
    }

    [Fact]
    public void QueryPoolDescription_RejectsStatisticsForTimestampQueries()
    {
        var description = new GraphicsQueryPoolDescription(
            GraphicsQueryType.Timestamp, 2,
            GraphicsPipelineStatistics.VertexShaderInvocations);

        Assert.Throws<ArgumentException>(description.Validate);
    }

    [Fact]
    public void AccelerationStructureDescription_RequiresGeometryForBottomLevel()
    {
        var description = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = false,
            Flags = GraphicsAccelerationStructureBuildFlags.AllowUpdate
        };

        Assert.Throws<ArgumentException>(description.Validate);
    }

    [Fact]
    public void AccelerationStructureDescription_RequiresInstanceCapacityForTopLevel()
    {
        var description = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = true,
            Flags = GraphicsAccelerationStructureBuildFlags.PreferFastTrace,
            MaximumInstanceCount = 0
        };

        Assert.Throws<ArgumentException>(description.Validate);
    }

    [Fact]
    public void RayTracingPipelineDescription_RequiresPipelineLayout()
    {
        var description = new GraphicsRayTracingPipelineDescription
        {
            Layout = null!,
            Stages = ReadOnlyMemory<GraphicsRayTracingShaderEntry>.Empty,
            Groups = ReadOnlyMemory<GraphicsRayTracingShaderGroup>.Empty,
            MaximumRecursionDepth = 0,
            MaximumPayloadSizeInBytes = 0,
            MaximumAttributeSizeInBytes = 0
        };

        Assert.Throws<ArgumentNullException>(description.Validate);
    }

    [Fact]
    public void BindingLayout_ValidatesVariableDescriptorRuntimeCount()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                3, GraphicsDescriptorType.SampledTexture, 1024,
                GraphicsShaderStages.Fragment,
                GraphicsBindingFlags.VariableCount | GraphicsBindingFlags.PartiallyBound)
        ]);

        layout.ValidateVariableCounts([new GraphicsVariableDescriptorCount(3, 256)]);
    }

    [Fact]
    public void BindingLayout_RejectsRuntimeCountAboveDeclaredMaximum()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                7, GraphicsDescriptorType.StorageBuffer, 64,
                GraphicsShaderStages.Compute, GraphicsBindingFlags.VariableCount)
        ]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            layout.ValidateVariableCounts([new GraphicsVariableDescriptorCount(7, 65)]));
    }

    [Fact]
    public void BindingLayout_RequiresExactlyOneRuntimeCountPerVariableBinding()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(
                1, GraphicsDescriptorType.SampledTexture, 16,
                GraphicsShaderStages.Fragment, GraphicsBindingFlags.VariableCount)
        ]);

        Assert.Throws<ArgumentException>(() => layout.ValidateVariableCounts([]));
    }
    [Fact]
    public void BindingLayout_ValidatesDynamicOffsetsInBindingOrder()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(5, GraphicsDescriptorType.DynamicStorageBuffer, 1, GraphicsShaderStages.Compute),
            new GraphicsBindingLayoutEntry(2, GraphicsDescriptorType.DynamicUniformBuffer, 2, GraphicsShaderStages.Vertex)
        ]);

        layout.ValidateDynamicOffsets(
        [
            new GraphicsDynamicBufferOffset(2, 0, 256),
            new GraphicsDynamicBufferOffset(2, 1, 512),
            new GraphicsDynamicBufferOffset(5, 0, 1024)
        ]);
    }

    [Fact]
    public void BindingLayout_RejectsMissingDynamicOffsets()
    {
        var layout = new GraphicsBindingLayoutDescription(
        [
            new GraphicsBindingLayoutEntry(0, GraphicsDescriptorType.DynamicUniformBuffer, 2, GraphicsShaderStages.Vertex)
        ]);

        Assert.Throws<ArgumentException>(() =>
            layout.ValidateDynamicOffsets([new GraphicsDynamicBufferOffset(0, 0, 256)]));
    }

    [Fact]
    public void MemoryRequirements_AcceptAlignedResourceRequirements()
    {
        var requirements = new GraphicsMemoryRequirements(
            4096, 256, 0b101, false, true);

        requirements.Validate();
    }

    [Theory]
    [InlineData(0, 256, 1)]
    [InlineData(1024, 0, 1)]
    [InlineData(1024, 3, 1)]
    [InlineData(1024, 256, 0)]
    public void MemoryRequirements_RejectInvalidSizeAlignmentOrMemoryTypes(
        ulong size, ulong alignment, uint memoryTypeBits)
    {
        var requirements = new GraphicsMemoryRequirements(
            size, alignment, memoryTypeBits, false, false);

        Assert.Throws<ArgumentOutOfRangeException>(requirements.Validate);
    }

    [Fact]
    public void MemoryRequirements_RejectRequiredButNotPreferredDedicatedAllocation()
    {
        var requirements = new GraphicsMemoryRequirements(
            1024, 256, 1, true, false);

        Assert.Throws<ArgumentException>(requirements.Validate);
    }

    [Fact]
    public void TimestampProperties_HandleCounterWraparound()
    {
        var properties = new GraphicsTimestampProperties(true, 1_000_000_000, 36);
        properties.Validate();

        ulong elapsed = properties.GetElapsedTicks((1UL << 36) - 5, 7);

        Assert.Equal(12UL, elapsed);
        Assert.Equal(0.000012, properties.GetElapsedMilliseconds((1UL << 36) - 5, 7), 9);
    }

    [Fact]
    public void TimestampProperties_RejectSupportedCounterWithoutFrequency()
    {
        var properties = new GraphicsTimestampProperties(true, 0, 32);
        Assert.Throws<ArgumentException>(properties.Validate);
    }

    [Fact]
    public void ShadingRateCapabilities_ValidateSupportedRateMask()
    {
        var capabilities = new GraphicsShadingRateCapabilities(16, 16, 4, 4, 1UL << (int)GraphicsShadingRate.Rate2x2);
        capabilities.Validate();
        Assert.True(capabilities.Supports(GraphicsShadingRate.Rate2x2));
        Assert.False(capabilities.Supports(GraphicsShadingRate.Rate4x4));
    }

    [Fact]
    public void DeviceCapabilities_AcceptDeviceGroupsAndShadingRateFormatSupport()
    {
        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.DeviceGroups | GraphicsFeature.VariableRateShading),
            GraphicsQueueCapabilities.Graphics,
            default,
            [new GraphicsFormatCapabilities(
                GraphicsFormat.R8UInt,
                GraphicsFormatSupport.ShadingRateAttachment,
                GraphicsSampleCountFlags.One)]);

        Assert.True(capabilities.Features.Supports(GraphicsFeature.DeviceGroups));
        Assert.True(capabilities.SupportsFormat(GraphicsFormat.R8UInt, GraphicsFormatSupport.ShadingRateAttachment));
    }

    [Fact]
    public void TextureDescription_AcceptsShadingRateAttachmentUsage()
    {
        var description = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.R8UInt,
            64, 64, 1, 1, 1, 1, GraphicsTextureUsage.ShadingRateAttachment);
        description.Validate();
    }

    [Fact]
    public void DeviceGroupMask_RejectsBitsOutsideDeviceCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphicsDeviceGroupMask(0b1000).Validate(3));
        Assert.Equal(uint.MaxValue, GraphicsDeviceGroupMask.All(32).Value);
    }

    [Fact]
    public void SparseResidencyCreation_RejectsConflictingMemoryOptions()
    {
        var buffer = new GraphicsBufferDescription(
            4096, GraphicsBufferUsage.Storage,
            CreationOptions: GraphicsResourceCreationOptions.SparseResidency |
                            GraphicsResourceCreationOptions.RequireExplicitMemoryBinding);
        Assert.Throws<ArgumentOutOfRangeException>(buffer.Validate);
    }

    [Fact]
    public void SparseResidencyCreation_AcceptsSparseTextureIntent()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.R8UInt,
            64, 64, 1, 1, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        texture.Validate();
    }

    [Fact]
    public void SparseTextureRequirements_RejectZeroTileSize()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 9, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        var requirements = new GraphicsSparseTextureRequirements(64, 64, 1, 0, 0, 0, 0, 0, false);

        Assert.Throws<ArgumentOutOfRangeException>(() => requirements.Validate(texture));
    }

    [Fact]
    public void SparseBufferRegion_RequiresWholeTileRanges()
    {
        var buffer = new GraphicsBufferDescription(4096, GraphicsBufferUsage.Storage);
        Assert.Throws<ArgumentException>(() =>
            new GraphicsSparseBufferRegion(1, 256).Validate(buffer, 256));
        new GraphicsSparseBufferRegion(256, 512).Validate(buffer, 256);
    }

    [Fact]
    public void ShaderBindingTableWriter_ComputesAlignedRecordLayout()
    {
        GraphicsShaderBindingTableRecord[] records =
        [
            new(0, new byte[12]),
            new(1, new byte[20])
        ];
        var properties = new GraphicsShaderBindingTableProperties(32, 32, 64, 4096);

        GraphicsShaderBindingTableLayout layout =
            GraphicsShaderBindingTableWriter.GetLayout(records, properties);

        Assert.Equal(64u, layout.StrideInBytes);
        Assert.Equal(128UL, layout.SizeInBytes);
    }

    [Fact]
    public void ShaderBindingTableProperties_RequirePowerOfTwoAlignment()
    {
        var properties = new GraphicsShaderBindingTableProperties(32, 24, 64, 4096);
        Assert.Throws<ArgumentException>(properties.Validate);
    }
}
