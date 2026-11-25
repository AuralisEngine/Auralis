using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Commands;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Pipelines;

namespace Auralis.Graphics.Resources;

/// <summary>Memory properties reported by a physical memory type. These are descriptive capabilities, not allocation guarantees.</summary>
[Flags]
public enum GraphicsMemoryProperty : uint
{
    None = 0,
    DeviceLocal = 1 << 0,
    HostVisible = 1 << 1,
    HostCoherent = 1 << 2,
    HostCached = 1 << 3,
    LazilyAllocated = 1 << 4,
    Protected = 1 << 5
}

/// <summary>Intended lifetime and access pattern used to guide backend memory placement.</summary>
public enum GraphicsMemoryUsage
{
    Unknown,
    GpuOnly,
    CpuToGpu,
    GpuToCpu,
    CpuOnly,
    TransientAttachment,
    Sparse
}

/// <summary>Describes a backend-reported memory heap. Heap sizes and budgets are snapshots.</summary>
public readonly record struct GraphicsMemoryHeapInfo(
    uint Index,
    ulong SizeInBytes,
    ulong BudgetInBytes,
    ulong UsageInBytes,
    bool IsDeviceLocal);

/// <summary>Describes a backend memory type compatible with one or more heaps.</summary>
public readonly record struct GraphicsMemoryTypeInfo(
    uint Index,
    uint HeapIndex,
    GraphicsMemoryProperty Properties,
    ulong MinimumAlignment);

/// <summary>Immutable memory allocation request. Zero alignment means use the resource's natural requirement.</summary>
public readonly record struct GraphicsMemoryAllocationDescription(
    ulong SizeInBytes,
    ulong Alignment,
    uint MemoryTypeBits,
    GraphicsMemoryProperty RequiredProperties,
    GraphicsMemoryProperty PreferredProperties,
    GraphicsMemoryUsage Usage,
    bool DedicatedRequired = false)
{
    public void Validate()
    {
        if (SizeInBytes == 0) throw new ArgumentOutOfRangeException(nameof(SizeInBytes));
        if (Alignment != 0 && (Alignment & (Alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(Alignment), "Alignment must be zero or a power of two.");
        if (MemoryTypeBits == 0) throw new ArgumentOutOfRangeException(nameof(MemoryTypeBits));
        if (!Enum.IsDefined(Usage)) throw new ArgumentOutOfRangeException(nameof(Usage));
        const GraphicsMemoryProperty allProperties =
            GraphicsMemoryProperty.DeviceLocal | GraphicsMemoryProperty.HostVisible |
            GraphicsMemoryProperty.HostCoherent | GraphicsMemoryProperty.HostCached |
            GraphicsMemoryProperty.LazilyAllocated | GraphicsMemoryProperty.Protected;
        if (((RequiredProperties | PreferredProperties) & ~allProperties) != 0)
            throw new ArgumentOutOfRangeException(nameof(RequiredProperties), "Memory property flags contain unknown values.");
    }
}

/// <summary>Opaque allocation owned by its allocator. Dispose releases the allocation, not necessarily its backing heap.</summary>
public interface IGraphicsMemoryAllocation : IDisposable
{
    IGraphicsDevice Device { get; }
    ulong SizeInBytes { get; }
    ulong OffsetInBytes { get; }
    uint MemoryTypeIndex { get; }
    GraphicsMemoryProperty Properties { get; }
    bool IsDedicated { get; }
    bool IsMapped { get; }
    Memory<byte> Map(ulong offset = 0, ulong size = ulong.MaxValue);
    void Unmap();
}

/// <summary>Explicit memory allocation service. Thread safety and suballocation policy are implementation-defined and documented by each backend.</summary>
public interface IGraphicsMemoryAllocator : IDisposable
{
    IGraphicsDevice Device { get; }
    ReadOnlyMemory<GraphicsMemoryHeapInfo> Heaps { get; }
    ReadOnlyMemory<GraphicsMemoryTypeInfo> MemoryTypes { get; }
    IGraphicsMemoryAllocation Allocate(in GraphicsMemoryAllocationDescription description);
}

/// <summary>Optional device extension exposing explicit memory allocation and heap information.</summary>
public interface IGraphicsMemoryDevice
{
    IGraphicsMemoryAllocator MemoryAllocator { get; }
}

/// <summary>Optional device extension for query-pool creation.</summary>
public interface IGraphicsQueryDevice
{
    IGraphicsDevice Device { get; }
    IGraphicsQueryPool CreateQueryPool(in GraphicsQueryPoolDescription description);
}

/// <summary>Optional resource-binding flags for descriptor arrays and streaming updates.</summary>
[Flags]
public enum GraphicsBindingFlags : uint
{
    None = 0,
    PartiallyBound = 1 << 0,
    UpdateAfterBind = 1 << 1,
    VariableDescriptorCount = 1 << 2,
    UpdateUnusedWhilePending = 1 << 3
}

/// <summary>Query category. Query availability is retrieved only after the producing queue has completed.</summary>
public enum GraphicsQueryType
{
    Timestamp,
    Occlusion,
    PipelineStatistics
}

[Flags]
public enum GraphicsPipelineStatistics : ulong
{
    None = 0,
    InputAssemblyVertices = 1UL << 0,
    InputAssemblyPrimitives = 1UL << 1,
    VertexShaderInvocations = 1UL << 2,
    GeometryShaderInvocations = 1UL << 3,
    GeometryShaderPrimitives = 1UL << 4,
    ClippingInvocations = 1UL << 5,
    ClippingPrimitives = 1UL << 6,
    FragmentShaderInvocations = 1UL << 7,
    TessellationControlPatches = 1UL << 8,
    TessellationEvaluationInvocations = 1UL << 9,
    ComputeShaderInvocations = 1UL << 10
}

/// <summary>Describes a fixed-size query range. Pipeline statistics selection is meaningful only for that query type.</summary>
public readonly record struct GraphicsQueryPoolDescription(
    GraphicsQueryType Type,
    uint QueryCount,
    GraphicsPipelineStatistics Statistics = GraphicsPipelineStatistics.None)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Type)) throw new ArgumentOutOfRangeException(nameof(Type));
        if (QueryCount == 0) throw new ArgumentOutOfRangeException(nameof(QueryCount));
        if (Type != GraphicsQueryType.PipelineStatistics && Statistics != GraphicsPipelineStatistics.None)
            throw new ArgumentException("Statistics flags are valid only for pipeline-statistics queries.");
        if (Type == GraphicsQueryType.PipelineStatistics && Statistics == GraphicsPipelineStatistics.None)
            throw new ArgumentException("At least one pipeline statistic must be selected.");
    }
}

/// <summary>Backend-neutral query results. Timestamp values are expressed in device timestamp ticks.</summary>
public readonly record struct GraphicsQueryResult(
    bool IsAvailable,
    ulong Value,
    GraphicsPipelineStatisticsResult? PipelineStatistics = null);

public readonly record struct GraphicsPipelineStatisticsResult(
    ulong InputAssemblyVertices,
    ulong InputAssemblyPrimitives,
    ulong VertexShaderInvocations,
    ulong GeometryShaderInvocations,
    ulong GeometryShaderPrimitives,
    ulong ClippingInvocations,
    ulong ClippingPrimitives,
    ulong FragmentShaderInvocations,
    ulong TessellationControlPatches,
    ulong TessellationEvaluationInvocations,
    ulong ComputeShaderInvocations);

/// <summary>Query storage. Reset and result reads must not race GPU writes to the same indices.</summary>
public interface IGraphicsQueryPool : IGraphicsDeviceObject
{
    GraphicsQueryPoolDescription Description { get; }
    void Reset(uint firstQuery, uint queryCount);
    bool TryGetResult(uint queryIndex, out GraphicsQueryResult result);
}

/// <summary>Ray-tracing geometry kind.</summary>
public enum GraphicsAccelerationStructureGeometryType
{
    Triangles,
    AxisAlignedBoundingBoxes
}

[Flags]
public enum GraphicsAccelerationStructureBuildFlags : uint
{
    None = 0,
    AllowUpdate = 1 << 0,
    AllowCompaction = 1 << 1,
    PreferFastTrace = 1 << 2,
    PreferFastBuild = 1 << 3,
    MinimizeMemory = 1 << 4
}

/// <summary>Triangle geometry backed by device buffers. Offsets and strides are byte-based.</summary>
public readonly record struct GraphicsRayTracingTriangleGeometry(
    IGraphicsBuffer VertexBuffer,
    ulong VertexOffset,
    uint VertexStride,
    uint VertexCount,
    GraphicsFormat VertexFormat,
    IGraphicsBuffer? IndexBuffer = null,
    ulong IndexOffset = 0,
    uint IndexCount = 0,
    GraphicsIndexFormat IndexFormat = GraphicsIndexFormat.UInt32,
    IGraphicsBuffer? TransformBuffer = null,
    ulong TransformOffset = 0);

/// <summary>Axis-aligned bounding-box geometry backed by a device buffer.</summary>
public readonly record struct GraphicsRayTracingAabbGeometry(
    IGraphicsBuffer Buffer,
    ulong Offset,
    uint Count,
    uint Stride);

/// <summary>One geometry entry in a bottom-level acceleration structure.</summary>
public readonly record struct GraphicsAccelerationStructureGeometry(
    GraphicsAccelerationStructureGeometryType Type,
    GraphicsRayTracingTriangleGeometry Triangles,
    GraphicsRayTracingAabbGeometry Aabbs,
    uint Flags = 0);

/// <summary>Build description for a bottom- or top-level acceleration structure.</summary>
public sealed record GraphicsAccelerationStructureDescription
{
    public required bool IsTopLevel { get; init; }
    public required GraphicsAccelerationStructureBuildFlags Flags { get; init; }
    public ReadOnlyMemory<GraphicsAccelerationStructureGeometry> Geometries { get; init; }
    public uint MaximumInstanceCount { get; init; }

    public void Validate()
    {
        const GraphicsAccelerationStructureBuildFlags allFlags =
            GraphicsAccelerationStructureBuildFlags.AllowUpdate |
            GraphicsAccelerationStructureBuildFlags.AllowCompaction |
            GraphicsAccelerationStructureBuildFlags.PreferFastTrace |
            GraphicsAccelerationStructureBuildFlags.PreferFastBuild |
            GraphicsAccelerationStructureBuildFlags.MinimizeMemory;
        if ((Flags & ~allFlags) != 0) throw new ArgumentOutOfRangeException(nameof(Flags));
        if (IsTopLevel)
        {
            if (MaximumInstanceCount == 0 || !Geometries.IsEmpty)
                throw new ArgumentException("Top-level structures require a non-zero instance capacity and no bottom-level geometry.");
        }
        else if (Geometries.IsEmpty || MaximumInstanceCount != 0)
        {
            throw new ArgumentException("Bottom-level structures require geometry and cannot declare an instance capacity.");
        }
    }
}

/// <summary>Opaque acceleration structure resource. Build storage and backing memory remain owned by the implementation.</summary>
public interface IGraphicsAccelerationStructure : IGraphicsResource
{
    bool IsTopLevel { get; }
    ulong DeviceAddress { get; }
}

/// <summary>Optional device contract for acceleration-structure creation and size queries.</summary>
public interface IGraphicsRayTracingDevice
{
    IGraphicsDevice Device { get; }
    GraphicsAccelerationStructureSizes GetAccelerationStructureSizes(in GraphicsAccelerationStructureDescription description);
    IGraphicsAccelerationStructure CreateAccelerationStructure(in GraphicsAccelerationStructureDescription description);
}

public readonly record struct GraphicsAccelerationStructureSizes(
    ulong AccelerationStructureSize,
    ulong BuildScratchSize,
    ulong UpdateScratchSize,
    ulong Alignment);

/// <summary>Shader table region expressed in bytes. Empty regions use a zero address and size.</summary>
public readonly record struct GraphicsShaderTableRegion(ulong DeviceAddress, ulong SizeInBytes, ulong StrideInBytes);

/// <summary>Ray dispatch dimensions and shader-table regions.</summary>
public readonly record struct GraphicsRayDispatchDescription(
    GraphicsShaderTableRegion RayGeneration,
    GraphicsShaderTableRegion Miss,
    GraphicsShaderTableRegion Hit,
    GraphicsShaderTableRegion Callable,
    uint Width,
    uint Height,
    uint Depth);

/// <summary>Ray-tracing pipeline stages have distinct execution semantics.</summary>
public enum GraphicsRayTracingShaderStage
{
    RayGeneration,
    Miss,
    ClosestHit,
    AnyHit,
    Intersection,
    Callable
}

/// <summary>Optional command-list extension for building acceleration structures and dispatching rays.</summary>
public interface IGraphicsRayTracingCommandList : IGraphicsCommandList
{
    void BuildAccelerationStructure(
        IGraphicsAccelerationStructure destination,
        IGraphicsAccelerationStructure? source,
        IGraphicsBuffer scratchBuffer,
        ulong scratchOffset,
        GraphicsAccelerationStructureBuildFlags flags);

    void DispatchRays(IGraphicsRayTracingPipeline pipeline, in GraphicsRayDispatchDescription description);
}

/// <summary>Ray-tracing pipeline contract. Shader artifacts and group composition are supplied by the application.</summary>
public interface IGraphicsRayTracingPipeline : IGraphicsPipeline
{
    GraphicsRayTracingPipelineDescription Description { get; }
}

/// <summary>Ray tracing pipeline stage and shader-group description; no shader compiler is implied.</summary>
public sealed record GraphicsRayTracingPipelineDescription
{
    public required IGraphicsPipelineLayout Layout { get; init; }
    public required ReadOnlyMemory<IGraphicsShader> Shaders { get; init; }
    public required uint MaximumRecursionDepth { get; init; }
    public required uint MaximumPayloadSizeInBytes { get; init; }
    public required uint MaximumAttributeSizeInBytes { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Layout);
        if (Shaders.IsEmpty) throw new ArgumentException("A ray-tracing pipeline requires at least one shader.");
        if (MaximumRecursionDepth == 0) throw new ArgumentOutOfRangeException(nameof(MaximumRecursionDepth));
    }
}

/// <summary>Optional device contract for ray-tracing pipeline creation.</summary>
public interface IGraphicsRayTracingPipelineDevice : IGraphicsRayTracingDevice
{
    IGraphicsRayTracingPipeline CreateRayTracingPipeline(GraphicsRayTracingPipelineDescription description);
}

/// <summary>Optional mesh-shader command-list contract.</summary>
public interface IGraphicsMeshCommandList : IGraphicsCommandList
{
    void DispatchMesh(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1);
    void DispatchMeshIndirect(IGraphicsBuffer arguments, ulong offset, uint drawCount = 1, uint stride = 0);
}

/// <summary>GPU marker scopes are diagnostic annotations and must not alter execution ordering.</summary>
public interface IGraphicsCommandDiagnostics
{
    void BeginLabel(string name, uint color = 0);
    void EndLabel();
    void InsertMarker(string name, uint color = 0);
}
