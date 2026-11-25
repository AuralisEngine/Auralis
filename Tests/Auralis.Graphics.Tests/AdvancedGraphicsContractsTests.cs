using Auralis.Graphics.Resources;
using Xunit;

namespace Auralis.Graphics.Tests;

public sealed class AdvancedGraphicsContractsTests
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
}
