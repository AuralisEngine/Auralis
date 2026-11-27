using Auralis.Graphics.Commands;
using System.Runtime.InteropServices;
using Auralis.Graphics.Resources;
using Auralis.Graphics.Pipelines;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Abstractions;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class AdvancedGraphicsTests
{
    [Fact]
    public void TimestampPropertiesValidateCounterWidthBeforeComputingMasks()
    {
        GraphicsTimestampProperties valid = new(true, 1_000_000, 32);
        Assert.Equal(0xFFFF_FFFFUL, valid.ValidMask);
        Assert.Equal(32UL, valid.GetElapsedTicks(0xFFFF_FFF0, 0x10));

        GraphicsTimestampProperties invalidWidth = new(true, 1_000_000, 65);
        Assert.Throws<ArgumentException>(() => _ = invalidWidth.ValidMask);
        Assert.Throws<ArgumentException>(() => invalidWidth.GetElapsedTicks(0, 1));

        GraphicsTimestampProperties inconsistentUnsupported = new(false, 1_000_000, 0);
        Assert.Throws<ArgumentException>(() => _ = inconsistentUnsupported.ValidMask);
    }

    [Fact]
    public void MemoryAllocationDescription_AcceptsPowerOfTwoAlignment()
    {
        var description = new GraphicsMemoryAllocationDescription(
            SizeInBytes: 4096,
            Alignment: 256,
            MemoryTypeBits: 0b101,
            RequiredProperties: GraphicsMemoryProperty.DeviceLocal,
            PreferredProperties: GraphicsMemoryProperty.HostVisible | GraphicsMemoryProperty.HostCached,
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
    public void MemoryAllocationDescription_RejectsContradictoryMemoryProperties()
    {
        var hostCached = new GraphicsMemoryAllocationDescription(
            1024, 256, 1, GraphicsMemoryProperty.HostCached,
            GraphicsMemoryProperty.None, GraphicsMemoryUsage.CpuToGpu);
        var lazyHostVisible = new GraphicsMemoryAllocationDescription(
            1024, 256, 1, GraphicsMemoryProperty.DeviceLocal | GraphicsMemoryProperty.HostVisible |
                GraphicsMemoryProperty.LazilyAllocated,
            GraphicsMemoryProperty.None, GraphicsMemoryUsage.TransientAttachment);
        Assert.Throws<ArgumentException>(hostCached.Validate);
        Assert.Throws<ArgumentException>(lazyHostVisible.Validate);
    }

    [Fact]
    public void MemoryAllocationDescription_AllowsIncompatiblePreferredHintsWhenRequiredPropertiesAreValid()
    {
        var upload = new GraphicsMemoryAllocationDescription(
            4096, 256, 1,
            GraphicsMemoryProperty.DeviceLocal | GraphicsMemoryProperty.HostVisible,
            GraphicsMemoryProperty.HostCached,
            GraphicsMemoryUsage.CpuToGpu);

        upload.Validate();
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
    public void AccelerationStructureGeometry_RejectsNonDefaultInactiveUnionMember()
    {
        var unusedAabb = new GraphicsRayTracingAabbGeometry(null!, 1, 0, 0);
        var geometry = new GraphicsAccelerationStructureGeometry(
            GraphicsAccelerationStructureGeometryType.Triangles,
            default,
            unusedAabb);
        Assert.Throws<ArgumentException>(geometry.Validate);
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
    public void DedicatedExplicitMemoryBindingRequiresDedicatedAllocationAtOffsetZero()
    {
        var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.None,
            GraphicsQueueCapabilities.Graphics);
        GraphicsResourceCreationOptions options =
            GraphicsResourceCreationOptions.RequireExplicitMemoryBinding |
            GraphicsResourceCreationOptions.DedicatedAllocation;
        var bufferDescription = new GraphicsBufferDescription(
            4096,
            GraphicsBufferUsage.Storage,
            CreationOptions: options);
        var textureDescription = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D,
            GraphicsFormat.RGBA8UNorm,
            64, 64, 1, 1, 1, 1,
            GraphicsTextureUsage.Sampled,
            options);
        bufferDescription.Validate();
        textureDescription.Validate();

        var requirements = new GraphicsMemoryRequirements(4096, 256, 1, false, false);
        var buffer = new TestMemoryBindingBuffer(device, bufferDescription);
        Assert.Throws<ArgumentException>(() =>
            new GraphicsResourceMemoryBinding(
                buffer, new TestMemoryBindingAllocation(device, 8192, isDedicated: false), 0)
                .Validate(requirements));

        var dedicatedAllocation = new TestMemoryBindingAllocation(device, 8192, isDedicated: true);
        new GraphicsResourceMemoryBinding(buffer, dedicatedAllocation, 0).Validate(requirements);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsResourceMemoryBinding(buffer, dedicatedAllocation, 256).Validate(requirements));
    }

    [Fact]
    public void ResourceMemoryBindingChecksLifetimeBeforeReadingDescriptions()
    {
        var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.None,
            GraphicsQueueCapabilities.Graphics);
        var description = new GraphicsBufferDescription(
            4096,
            GraphicsBufferUsage.Storage,
            CreationOptions: GraphicsResourceCreationOptions.RequireExplicitMemoryBinding);
        var requirements = new GraphicsMemoryRequirements(4096, 256, 1, false, false);
        var allocation = new TestMemoryBindingAllocation(device, 8192);
        var disposedBuffer = new TestMemoryBindingBuffer(device, description);
        disposedBuffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            new GraphicsResourceMemoryBinding(disposedBuffer, allocation, 0).Validate(requirements));
        Assert.Equal(0, disposedBuffer.DescriptionReadCount);

        var liveBuffer = new TestMemoryBindingBuffer(device, description);
        var disposedAllocation = new TestMemoryBindingAllocation(device, 8192);
        disposedAllocation.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            new GraphicsResourceMemoryBinding(liveBuffer, disposedAllocation, 0).Validate(requirements));
        Assert.Equal(0, liveBuffer.DescriptionReadCount);

        new GraphicsResourceMemoryBinding(
            liveBuffer,
            new TestMemoryBindingAllocation(device, 8192),
            256).Validate(requirements);
        Assert.Equal(1, liveBuffer.DescriptionReadCount);
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
        var capabilities = new GraphicsShadingRateCapabilities(
            16, 16, 4, 4,
            (1UL << (int)GraphicsShadingRate.Rate1x1) | (1UL << (int)GraphicsShadingRate.Rate2x2));
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
    public void SparseTextureDescription_AllowsMultisampleIntentForBackendCapabilityValidation()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 1, 1, 4, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        texture.Validate();
    }

    [Fact]
    public void BufferDescription_RejectsNonPowerOfTwoMinimumAlignment()
    {
        var description = new GraphicsBufferDescription(
            1024, GraphicsBufferUsage.Storage, MinimumAlignment: 3);
        Assert.Throws<ArgumentOutOfRangeException>(description.Validate);
    }

    [Fact]
    public void SparseTextureDescription_RejectsOneDimensionalSparseResidency()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture1D, GraphicsFormat.R8UNorm,
            256, 1, 1, 9, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        Assert.Throws<ArgumentException>(texture.Validate);
    }

    [Fact]
    public void SparseTextureRequirements_RejectMissingMipTailBoundary()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 9, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        var requirements = new GraphicsSparseTextureRequirements(64, 64, 1, 65536, 65536, 4, 0, 0, 0, false);
        Assert.Throws<ArgumentException>(() => requirements.Validate(texture));
    }

    [Fact]
    public void SparseTextureRequirements_AcceptsBackendReportedNonPowerOfTwoTileExtents()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 9, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        var requirements = new GraphicsSparseTextureRequirements(
            48, 24, 1, 65536, 65536, 4, 65536, 0, 0, false);

        requirements.Validate(texture);
    }

    [Fact]
    public void SparseTextureRequirements_RejectZeroTileSize()
    {
        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            256, 256, 1, 9, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.SparseResidency);
        var requirements = new GraphicsSparseTextureRequirements(64, 64, 1, 0, 65536, 0, 0, 0, 0, false);

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
    public void RayTracingInstanceData_HasCanonical64ByteLayout()
    {
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<GraphicsRayTracingInstanceData>());
        Assert.Equal((nint)48, System.Runtime.InteropServices.Marshal.OffsetOf<GraphicsRayTracingInstanceData>(
            nameof(GraphicsRayTracingInstanceData.InstanceCustomIndexAndMask)));
        Assert.Equal((nint)52, System.Runtime.InteropServices.Marshal.OffsetOf<GraphicsRayTracingInstanceData>(
            nameof(GraphicsRayTracingInstanceData.ShaderBindingTableRecordOffsetAndFlags)));
        Assert.Equal((nint)56, System.Runtime.InteropServices.Marshal.OffsetOf<GraphicsRayTracingInstanceData>(
            nameof(GraphicsRayTracingInstanceData.BottomLevelDeviceAddress)));
    }

    [Fact]
    public void ShaderBindingTableWriterFetchesOnlyUniqueReferencedGroupRuns()
    {
        var device = new GraphicsDeviceFactoryTests.TestDevice(
            GraphicsBackend.Vulkan,
            GraphicsFeature.RayTracing,
            GraphicsQueueCapabilities.Graphics | GraphicsQueueCapabilities.Compute);
        var properties = new GraphicsShaderBindingTableProperties(4, 4, 16, 128);
        var pipelineLayout = new TestRayTracingPipelineLayout(device);
        GraphicsRayTracingPipelineDescription description = new()
        {
            Layout = pipelineLayout,
            Stages = ReadOnlyMemory<GraphicsRayTracingShaderEntry>.Empty,
            Groups = new GraphicsRayTracingShaderGroup[100],
            MaximumRecursionDepth = 1,
            MaximumPayloadSizeInBytes = 0,
            MaximumAttributeSizeInBytes = 0
        };
        var pipeline = new TestRayTracingPipeline(device, pipelineLayout, description);
        var tableDevice = new TestRayTracingShaderTableDevice(device, properties);
        GraphicsShaderBindingTableRecord[] records =
        [
            new(2, new byte[] { 0xA1 }),
            new(99, new byte[] { 0xB2 }),
            new(2, new byte[] { 0xC3 })
        ];
        GraphicsShaderBindingTableLayout layout =
            GraphicsShaderBindingTableWriter.GetLayout(records, properties);
        byte[] destination = new byte[checked((int)layout.SizeInBytes)];
        byte[] handleScratch = new byte[2 * (int)properties.ShaderGroupHandleSize];
        uint[] groupIndexScratch = new uint[records.Length];

        Assert.Equal(12,
            GraphicsShaderBindingTableWriter.GetHandleScratchSizeUpperBound(records.Length, properties));

        uint[] aliasedStorage = [0xAABBCCDD, 0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00, 0x12345678];
        uint[] originalAliasedStorage = [.. aliasedStorage];
        Assert.Throws<ArgumentException>(() => InvokeShaderTableWriterWithAliasedSpans(
            tableDevice, pipeline, records, handleScratch, aliasedStorage));
        Assert.Equal(originalAliasedStorage, aliasedStorage);

        GraphicsShaderBindingTableWriter.WriteRecords(
            tableDevice, pipeline, records, destination, handleScratch, groupIndexScratch);

        Assert.Equal(new (uint FirstGroup, uint GroupCount)[] { (2, 1), (99, 1) },
            tableDevice.RequestedRanges);
        Assert.Equal((byte)2, destination[0]);
        Assert.Equal((byte)0xA1, destination[4]);
        Assert.Equal((byte)99, destination[8]);
        Assert.Equal((byte)0xB2, destination[12]);
        Assert.Equal((byte)2, destination[16]);
        Assert.Equal((byte)0xC3, destination[20]);
        Assert.Equal(2u, groupIndexScratch[0]);
        Assert.Equal(99u, groupIndexScratch[1]);
    }

    [Fact]
    public void ShaderBindingTableProperties_RequirePowerOfTwoAlignment()
    {
        var properties = new GraphicsShaderBindingTableProperties(32, 24, 64, 4096);
        Assert.Throws<ArgumentException>(properties.Validate);
    }

    [Fact]
    public void ShaderBindingTableProperties_RequireBaseAlignmentAtLeastHandleAlignment()
    {
        var properties = new GraphicsShaderBindingTableProperties(32, 64, 32, 4096);
        Assert.Throws<ArgumentException>(properties.Validate);
    }

    [Fact]
    public void ShaderTableRegion_ValidatesDeviceAlignmentWhenPropertiesAreSupplied()
    {
        var properties = new GraphicsShaderBindingTableProperties(32, 32, 64, 4096);
        new GraphicsShaderTableRegion(128, 64, 32).Validate(properties);
        Assert.Throws<ArgumentException>(() =>
            new GraphicsShaderTableRegion(96, 64, 32).Validate(properties));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GraphicsShaderTableRegion(128, 72, 24).Validate(properties));
    }

    [Fact]
    public void RayDispatchDescription_ValidatesDeviceShaderTableProperties()
    {
        var properties = new GraphicsShaderBindingTableProperties(32, 32, 64, 4096);
        var description = new GraphicsRayDispatchDescription(
            new GraphicsShaderTableRegion(128, 32, 32), default, default, default, 1, 1, 1);
        description.Validate(properties);
        Assert.Throws<ArgumentException>(() =>
            (description with { Miss = new GraphicsShaderTableRegion(96, 64, 32) }).Validate(properties));
    }

    [Fact]
    public void QueryPoolDescription_RejectsUnknownStatisticsFlags()
    {
        var description = new GraphicsQueryPoolDescription(
            GraphicsQueryType.PipelineStatistics, 1, (GraphicsPipelineStatistics)(1UL << 63));
        Assert.Throws<ArgumentOutOfRangeException>(description.Validate);
    }

    [Fact]
    public void ShadingRateCapabilities_RequireBaselineAndPowerOfTwoDimensions()
    {
        var missingBaseline = new GraphicsShadingRateCapabilities(16, 16, 4, 4,
            1UL << (int)GraphicsShadingRate.Rate2x2);
        var invalidTile = new GraphicsShadingRateCapabilities(12, 16, 4, 4,
            (1UL << (int)GraphicsShadingRate.Rate1x1) | (1UL << (int)GraphicsShadingRate.Rate2x2));
        var invalidFragmentSize = new GraphicsShadingRateCapabilities(16, 16, 3, 4,
            (1UL << (int)GraphicsShadingRate.Rate1x1) | (1UL << (int)GraphicsShadingRate.Rate2x2));
        Assert.Throws<ArgumentException>(missingBaseline.Validate);
        Assert.Throws<ArgumentException>(invalidTile.Validate);
        Assert.Throws<ArgumentException>(invalidFragmentSize.Validate);
    }

    [Fact]
    public void ShadingRateRequestValidatesSupportedRateAndDefinedCombiners()
    {
        GraphicsShadingRateCapabilities capabilities = new(
            16, 16, 4, 4,
            (1UL << (int)GraphicsShadingRate.Rate1x1) |
            (1UL << (int)GraphicsShadingRate.Rate2x2));

        capabilities.ValidateRequest(
            GraphicsShadingRate.Rate2x2,
            GraphicsShadingRateCombiner.Min,
            GraphicsShadingRateCombiner.Multiply);
        Assert.Throws<ArgumentException>(() => capabilities.ValidateRequest(
            GraphicsShadingRate.Rate4x4,
            GraphicsShadingRateCombiner.Min,
            GraphicsShadingRateCombiner.Max));
        Assert.Throws<ArgumentOutOfRangeException>(() => capabilities.ValidateRequest(
            (GraphicsShadingRate)99,
            GraphicsShadingRateCombiner.Min,
            GraphicsShadingRateCombiner.Max));
        Assert.Throws<ArgumentOutOfRangeException>(() => capabilities.ValidateRequest(
            GraphicsShadingRate.Rate1x1,
            (GraphicsShadingRateCombiner)99,
            GraphicsShadingRateCombiner.Max));
        Assert.Throws<ArgumentOutOfRangeException>(() => capabilities.ValidateRequest(
            GraphicsShadingRate.Rate1x1,
            GraphicsShadingRateCombiner.Min,
            (GraphicsShadingRateCombiner)99));
    }

    [Fact]
    public void ShadingRateCapabilitiesRejectUnknownRateBits()
    {
        GraphicsShadingRateCapabilities capabilities = new(
            16, 16, 4, 4,
            (1UL << (int)GraphicsShadingRate.Rate1x1) | (1UL << 63));
        Assert.Throws<ArgumentOutOfRangeException>(capabilities.Validate);
    }

    [Fact]
    public void TextureViewFormatCompatibilityIsDeviceReportedAndDefensivelyCopied()
    {
        GraphicsFormat[] compatibleFormats = [GraphicsFormat.RGBA8UNormSrgb];
        GraphicsFormatCapabilities[] formats =
        [
            new(GraphicsFormat.RGBA8UNorm, GraphicsFormatSupport.Sampled, GraphicsSampleCountFlags.One, compatibleFormats),
            new(GraphicsFormat.RGBA8UNormSrgb, GraphicsFormatSupport.Sampled, GraphicsSampleCountFlags.One)
        ];
        var capabilities = new GraphicsDeviceCapabilities(
            new GraphicsFeatureSet(GraphicsFeature.None), GraphicsQueueCapabilities.Graphics, default, formats);
        compatibleFormats[0] = GraphicsFormat.R32UInt;

        var texture = new GraphicsTextureDescription(
            GraphicsTextureDimension.Texture2D, GraphicsFormat.RGBA8UNorm,
            32, 32, 1, 1, 1, 1, GraphicsTextureUsage.Sampled,
            GraphicsResourceCreationOptions.MutableFormat);
        var view = new GraphicsTextureViewDescription(
            GraphicsFormat.RGBA8UNormSrgb, GraphicsTextureViewType.Texture2D, 0, 1, 0, 1);
        capabilities.ValidateTextureViewDescription(texture, view);
    }

    [Fact]
    public void QueryPoolDescription_AcceptsCompactedSizeQueryType()
    {
        new GraphicsQueryPoolDescription(GraphicsQueryType.AccelerationStructureCompactedSize, 1).Validate();
    }

    [Fact]
    public void RayTracingGeometryValidation_RejectsNullBuffersAndInvalidFlags()
    {
        var geometry = new GraphicsRayTracingTriangleGeometry(null!, 0, 12, 3, GraphicsFormat.RGB32Float);
        Assert.Throws<ArgumentNullException>(geometry.Validate);
        var description = new GraphicsAccelerationStructureDescription
        {
            IsTopLevel = false,
            Flags = GraphicsAccelerationStructureBuildFlags.None,
            Geometries = new[]
            {
                new GraphicsAccelerationStructureGeometry(
                    GraphicsAccelerationStructureGeometryType.Triangles, geometry, default,
                    (GraphicsRayTracingGeometryFlags)0x80)
            }
        };
        Assert.Throws<ArgumentOutOfRangeException>(description.Validate);
    }


    private sealed class TestRayTracingPipelineLayout(IGraphicsDevice device)
        : GraphicsDeviceObject(device), IGraphicsPipelineLayout
    {
        public ReadOnlyMemory<IGraphicsBindingLayout> BindingLayouts => ReadOnlyMemory<IGraphicsBindingLayout>.Empty;

        protected override void DisposeCore() { }
    }

    private sealed class TestRayTracingPipeline(
        IGraphicsDevice device,
        IGraphicsPipelineLayout layout,
        GraphicsRayTracingPipelineDescription description)
        : GraphicsDeviceObject(device), IGraphicsRayTracingPipeline
    {
        public string? DebugName => null;
        public IGraphicsPipelineLayout Layout { get; } = layout;
        public GraphicsRayTracingPipelineDescription Description { get; } = description;

        protected override void DisposeCore() { }
    }

    private sealed class TestRayTracingShaderTableDevice(
        IGraphicsDevice device,
        GraphicsShaderBindingTableProperties properties)
        : IGraphicsRayTracingShaderTableDevice
    {
        public IGraphicsDevice Device { get; } = device;
        public GraphicsShaderBindingTableProperties ShaderBindingTableProperties { get; } = properties;
        public List<(uint FirstGroup, uint GroupCount)> RequestedRanges { get; } = [];

        public GraphicsAccelerationStructureSizes GetAccelerationStructureSizes(
            in GraphicsAccelerationStructureDescription description) =>
            throw new NotSupportedException();

        public IGraphicsAccelerationStructure CreateAccelerationStructure(
            in GraphicsAccelerationStructureDescription description) =>
            throw new NotSupportedException();

        public IGraphicsRayTracingPipeline CreateRayTracingPipeline(
            GraphicsRayTracingPipelineDescription description) =>
            throw new NotSupportedException();

        public void GetShaderGroupHandles(
            IGraphicsRayTracingPipeline pipeline,
            uint firstGroup,
            Span<byte> destination)
        {
            int handleSize = checked((int)ShaderBindingTableProperties.ShaderGroupHandleSize);
            if (destination.Length % handleSize != 0)
                throw new ArgumentException("Handle retrieval must request a whole number of handles.", nameof(destination));
            uint groupCount = (uint)(destination.Length / handleSize);
            RequestedRanges.Add((firstGroup, groupCount));
            for (uint group = 0; group < groupCount; group++)
                destination.Slice(checked((int)group * handleSize), handleSize)
                    .Fill(checked((byte)(firstGroup + group)));
        }
    }



    /// <summary>Invokes the SBT writer with destination and index scratch deliberately aliased.</summary>
    private static void InvokeShaderTableWriterWithAliasedSpans(
        IGraphicsRayTracingShaderTableDevice device,
        IGraphicsRayTracingPipeline pipeline,
        ReadOnlySpan<GraphicsShaderBindingTableRecord> records,
        byte[] handleScratch,
        uint[] aliasedStorage)
    {
        Span<byte> aliasedBytes = MemoryMarshal.AsBytes(aliasedStorage.AsSpan());
        Span<uint> aliasedIndices = MemoryMarshal.Cast<byte, uint>(
            aliasedBytes[..(records.Length * sizeof(uint))]);
        GraphicsShaderBindingTableWriter.WriteRecords(
            device, pipeline, records, aliasedBytes, handleScratch, aliasedIndices);
    }



    private sealed class TestMemoryBindingBuffer(
        IGraphicsDevice device,
        GraphicsBufferDescription description)
        : GraphicsDeviceObject(device), IGraphicsBuffer
    {
        public int DescriptionReadCount { get; private set; }
        public string? DebugName => null;

        public GraphicsBufferDescription Description
        {
            get
            {
                DescriptionReadCount++;
                if (IsDisposed)
                    throw new InvalidOperationException("A disposed buffer description must not be accessed.");
                return description;
            }
        }

        public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();

        public void Unmap() => throw new NotSupportedException();

        protected override void DisposeCore() { }
    }

    private sealed class TestMemoryBindingAllocation(
        IGraphicsDevice device,
        ulong sizeInBytes,
        bool isDedicated = false)
        : GraphicsDeviceObject(device), IGraphicsMemoryAllocation
    {
        public ulong SizeInBytes { get; } = sizeInBytes;
        public ulong OffsetInBytes => 0;
        public uint MemoryTypeIndex => 0;
        public GraphicsMemoryProperty Properties => GraphicsMemoryProperty.DeviceLocal;
        public bool IsDedicated { get; } = isDedicated;
        public bool IsMapped => false;

        public Memory<byte> Map(ulong offset = 0, ulong size = ulong.MaxValue) =>
            throw new NotSupportedException();

        public void Unmap() => throw new NotSupportedException();

        protected override void DisposeCore() { }
    }


}
