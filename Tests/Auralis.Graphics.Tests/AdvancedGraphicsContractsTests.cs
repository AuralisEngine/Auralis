using Auralis.Graphics.Resources;
using Auralis.Graphics.Pipelines;
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

}
