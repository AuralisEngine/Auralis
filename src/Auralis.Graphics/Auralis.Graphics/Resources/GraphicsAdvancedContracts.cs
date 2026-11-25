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
    GraphicsMemoryProperty Properties);

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

        ValidatePropertyCombination(RequiredProperties, nameof(RequiredProperties));
    }

    private static void ValidatePropertyCombination(GraphicsMemoryProperty properties, string parameterName)
    {
        const GraphicsMemoryProperty hostVisibilityProperties =
            GraphicsMemoryProperty.HostCoherent | GraphicsMemoryProperty.HostCached;
        if ((properties & hostVisibilityProperties) != 0 &&
            (properties & GraphicsMemoryProperty.HostVisible) == 0)
            throw new ArgumentException("Host-coherent and host-cached memory must also be host-visible.", parameterName);

        if ((properties & GraphicsMemoryProperty.LazilyAllocated) != 0 &&
            ((properties & GraphicsMemoryProperty.DeviceLocal) == 0 ||
             (properties & GraphicsMemoryProperty.HostVisible) != 0))
            throw new ArgumentException("Lazily allocated memory must be device-local and not host-visible.", parameterName);

        if ((properties & GraphicsMemoryProperty.Protected) != 0 &&
            (properties & GraphicsMemoryProperty.HostVisible) != 0)
            throw new ArgumentException("Protected memory cannot be host-visible.", parameterName);
    }
}

/// <summary>Opaque allocation owned by its allocator. Dispose releases the allocation, not necessarily its backing heap.</summary>
public interface IGraphicsMemoryAllocation : IGraphicsDeviceObject
{
    ulong SizeInBytes { get; }
    ulong OffsetInBytes { get; }
    uint MemoryTypeIndex { get; }
    GraphicsMemoryProperty Properties { get; }
    bool IsDedicated { get; }
    bool IsMapped { get; }
    /// <summary>
    /// Maps one allocation window. Because Memory&lt;byte&gt; has an Int32 length, a single window cannot exceed
    /// Int32.MaxValue bytes; larger allocations must be accessed through multiple explicit windows.
    /// </summary>
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

/// <summary>Query category. Query availability is retrieved only after the producing queue has completed.</summary>
public enum GraphicsQueryType
{
    Timestamp,
    Occlusion,
    PipelineStatistics,
    /// <summary>GPU-generated compacted size of an acceleration structure.</summary>
    AccelerationStructureCompactedSize
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
        const GraphicsPipelineStatistics allStatistics =
            GraphicsPipelineStatistics.InputAssemblyVertices |
            GraphicsPipelineStatistics.InputAssemblyPrimitives |
            GraphicsPipelineStatistics.VertexShaderInvocations |
            GraphicsPipelineStatistics.GeometryShaderInvocations |
            GraphicsPipelineStatistics.GeometryShaderPrimitives |
            GraphicsPipelineStatistics.ClippingInvocations |
            GraphicsPipelineStatistics.ClippingPrimitives |
            GraphicsPipelineStatistics.FragmentShaderInvocations |
            GraphicsPipelineStatistics.TessellationControlPatches |
            GraphicsPipelineStatistics.TessellationEvaluationInvocations |
            GraphicsPipelineStatistics.ComputeShaderInvocations;
        if ((Statistics & ~allStatistics) != 0)
            throw new ArgumentOutOfRangeException(nameof(Statistics));
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
    ulong TransformOffset = 0)
{
    public void Validate()
    {
            if (VertexCount < 3)
            throw new ArgumentOutOfRangeException(nameof(VertexCount));
        ulong vertexElementSize = VertexFormat switch
        {
            GraphicsFormat.RG16Float => 4UL,
            GraphicsFormat.RGBA16Float => 8UL,
            GraphicsFormat.RGB32Float => 12UL,
            GraphicsFormat.RGBA32Float => 16UL,
            _ => throw new ArgumentException(
                "Triangle positions must use RG16Float, RGBA16Float, RGB32Float, or RGBA32Float.",
                nameof(VertexFormat))
        };
        uint alignment = VertexFormat is GraphicsFormat.RG16Float or GraphicsFormat.RGBA16Float ? 2u : 4u;
        if (VertexStride < vertexElementSize || VertexStride % alignment != 0 || VertexOffset % alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(VertexStride),
                "Triangle vertex stride and offset must satisfy the selected format's element size and alignment.");
        ulong vertexDataSize = checked((ulong)(VertexCount - 1) * VertexStride + vertexElementSize);
        GraphicsRayTracingBufferValidation.Validate(
            VertexBuffer, VertexOffset, vertexDataSize, nameof(VertexBuffer));
        GraphicsRayTracingBufferValidation.RequireAddressAlignment(VertexBuffer, VertexOffset, 4, nameof(VertexOffset));

        if (IndexBuffer is null)
        {
            if (IndexCount != 0 || IndexOffset != 0)
                throw new ArgumentException("Index count and offset must be zero when no index buffer is supplied.");
            if (VertexCount % 3 != 0)
                throw new ArgumentException("Non-indexed triangle vertex counts must be a multiple of three.", nameof(VertexCount));
        }
        else
        {
            if (!Enum.IsDefined(IndexFormat) || IndexCount == 0 || IndexCount % 3 != 0)
                throw new ArgumentException("Indexed triangle geometry requires a valid index format and a non-zero multiple-of-three index count.");
            ulong indexElementSize = IndexFormat == GraphicsIndexFormat.UInt16 ? 2UL : 4UL;
            GraphicsRayTracingBufferValidation.Validate(
                IndexBuffer, IndexOffset, checked((ulong)IndexCount * indexElementSize), nameof(IndexBuffer));
            if (IndexOffset % indexElementSize != 0)
                throw new ArgumentOutOfRangeException(nameof(IndexOffset));
            GraphicsRayTracingBufferValidation.RequireAddressAlignment(
                IndexBuffer, IndexOffset, indexElementSize, nameof(IndexOffset));
            GraphicsRayTracingBufferValidation.RequireSameDevice(VertexBuffer, IndexBuffer, nameof(IndexBuffer));
        }

        if (TransformBuffer is not null)
        {
            GraphicsRayTracingBufferValidation.Validate(TransformBuffer, TransformOffset, 48, nameof(TransformBuffer));
            if ((TransformOffset & 15) != 0)
                throw new ArgumentOutOfRangeException(nameof(TransformOffset), "The 3x4 transform must be 16-byte aligned.");
            GraphicsRayTracingBufferValidation.RequireAddressAlignment(
                TransformBuffer, TransformOffset, 16, nameof(TransformOffset));
            GraphicsRayTracingBufferValidation.RequireSameDevice(VertexBuffer, TransformBuffer, nameof(TransformBuffer));
        }
        if (!Enum.IsDefined(IndexFormat))
            throw new ArgumentOutOfRangeException(nameof(IndexFormat));
    }
}

/// <summary>Axis-aligned bounding-box geometry backed by a device buffer.</summary>
public readonly record struct GraphicsRayTracingAabbGeometry(
    IGraphicsBuffer Buffer,
    ulong Offset,
    uint Count,
    uint Stride)
{
    public void Validate()
    {
        if (Count == 0 || Stride < 24 || (Stride & 7) != 0 || (Offset & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(Stride),
                "AABB geometry requires a non-zero count, an eight-byte-aligned stride of at least 24 bytes, and an eight-byte-aligned offset.");
        ulong aabbDataSize = checked((ulong)(Count - 1) * Stride + 24);
        GraphicsRayTracingBufferValidation.Validate(Buffer, Offset, aabbDataSize, nameof(Buffer));
        GraphicsRayTracingBufferValidation.RequireAddressAlignment(Buffer, Offset, 8, nameof(Offset));
    }
}

[Flags]
public enum GraphicsRayTracingGeometryFlags : uint
{
    None = 0,
    Opaque = 1 << 0,
    NoDuplicateAnyHitInvocation = 1 << 1
}

/// <summary>One geometry entry in a bottom-level acceleration structure.</summary>
public readonly record struct GraphicsAccelerationStructureGeometry(
    GraphicsAccelerationStructureGeometryType Type,
    GraphicsRayTracingTriangleGeometry Triangles,
    GraphicsRayTracingAabbGeometry Aabbs,
    GraphicsRayTracingGeometryFlags Flags = GraphicsRayTracingGeometryFlags.None)
{
    public void Validate()
    {
        const GraphicsRayTracingGeometryFlags allFlags =
            GraphicsRayTracingGeometryFlags.Opaque | GraphicsRayTracingGeometryFlags.NoDuplicateAnyHitInvocation;
        if (!Enum.IsDefined(Type))
            throw new ArgumentOutOfRangeException(nameof(Type));
        if ((Flags & ~allFlags) != 0)
            throw new ArgumentOutOfRangeException(nameof(Flags));
        if (Type == GraphicsAccelerationStructureGeometryType.Triangles)
        {
            if (Aabbs != default)
                throw new ArgumentException("Triangle geometry must not populate the inactive AABB member.", nameof(Aabbs));
            Triangles.Validate();
        }
        else
        {
            if (Triangles != default)
                throw new ArgumentException("AABB geometry must not populate the inactive triangle member.", nameof(Triangles));
            Aabbs.Validate();
        }
    }

    internal IGraphicsDevice Device => Type == GraphicsAccelerationStructureGeometryType.Triangles
        ? Triangles.VertexBuffer.Device
        : Aabbs.Buffer.Device;
}

internal static class GraphicsRayTracingBufferValidation
{
    public static void Validate(IGraphicsBuffer buffer, ulong offset, ulong size, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.IsDisposed || buffer.Device.IsDisposed)
            throw new ObjectDisposedException(parameterName);
        buffer.Description.Validate();
        if ((buffer.Description.Usage & GraphicsBufferUsage.AccelerationStructureBuildInput) == 0)
            throw new ArgumentException("Ray-tracing geometry buffers must declare AccelerationStructureBuildInput usage.", parameterName);
        if (buffer is not IGraphicsBufferDeviceAddress address || address.DeviceAddress == 0)
            throw new ArgumentException("Ray-tracing geometry buffers must expose a non-zero GPU device address.", parameterName);
        _ = checked(address.DeviceAddress + offset);
        if (size == 0 || offset > buffer.Description.SizeInBytes ||
            size > buffer.Description.SizeInBytes - offset)
            throw new ArgumentOutOfRangeException(parameterName, "Ray-tracing geometry range exceeds its buffer.");
    }

    public static void RequireAddressAlignment(IGraphicsBuffer buffer, ulong offset, ulong alignment, string parameterName)
    {
        if (buffer is not IGraphicsBufferDeviceAddress address ||
            alignment == 0 || checked(address.DeviceAddress + offset) % alignment != 0)
            throw new ArgumentException("The resulting GPU device address does not satisfy the required alignment.", parameterName);
    }

    public static void RequireSameDevice(IGraphicsBuffer first, IGraphicsBuffer second, string parameterName)
    {
        if (!ReferenceEquals(first.Device, second.Device))
            throw new ArgumentException("All buffers in a ray-tracing geometry must belong to the same device.", parameterName);
    }
}

/// <summary>12-float affine transform used to describe a ray-tracing instance.</summary>
public readonly record struct GraphicsTransform3x4(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34)
{
    public static GraphicsTransform3x4 Identity => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

    public void Validate()
    {
        if (!float.IsFinite(M11) || !float.IsFinite(M12) || !float.IsFinite(M13) || !float.IsFinite(M14) ||
            !float.IsFinite(M21) || !float.IsFinite(M22) || !float.IsFinite(M23) || !float.IsFinite(M24) ||
            !float.IsFinite(M31) || !float.IsFinite(M32) || !float.IsFinite(M33) || !float.IsFinite(M34))
            throw new ArgumentException("Ray-tracing instance transforms must contain only finite values.");
    }
}

[Flags]
public enum GraphicsRayTracingInstanceFlags : uint
{
    None = 0,
    TriangleFacingCullDisable = 1 << 0,
    TriangleFlipFacing = 1 << 1,
    ForceOpaque = 1 << 2,
    ForceNonOpaque = 1 << 3
}

/// <summary>One top-level acceleration-structure instance, snapshotted by the backend during command recording.</summary>
public readonly record struct GraphicsRayTracingInstance(
    IGraphicsAccelerationStructure BottomLevel,
    GraphicsTransform3x4 Transform,
    uint InstanceCustomIndex = 0,
    byte Mask = byte.MaxValue,
    uint ShaderBindingTableRecordOffset = 0,
    GraphicsRayTracingInstanceFlags Flags = GraphicsRayTracingInstanceFlags.None)
{
    public void Validate(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsDisposed)
            throw new ObjectDisposedException(nameof(device));
        ArgumentNullException.ThrowIfNull(BottomLevel);
        if (BottomLevel.IsDisposed || BottomLevel.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(BottomLevel));
        if (!ReferenceEquals(BottomLevel.Device, device) || BottomLevel.IsTopLevel || BottomLevel.DeviceAddress == 0)
            throw new ArgumentException("A TLAS instance must reference a live, device-addressable BLAS from the same device.");
        if (InstanceCustomIndex > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(InstanceCustomIndex),
                "Instance custom indices are limited to 24 bits.");
        if (ShaderBindingTableRecordOffset > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(ShaderBindingTableRecordOffset),
                "Shader-binding-table record offsets are limited to 24 bits.");
        const GraphicsRayTracingInstanceFlags allFlags =
            GraphicsRayTracingInstanceFlags.TriangleFacingCullDisable |
            GraphicsRayTracingInstanceFlags.TriangleFlipFacing |
            GraphicsRayTracingInstanceFlags.ForceOpaque |
            GraphicsRayTracingInstanceFlags.ForceNonOpaque;
        if ((Flags & ~allFlags) != 0 ||
            (Flags & (GraphicsRayTracingInstanceFlags.ForceOpaque | GraphicsRayTracingInstanceFlags.ForceNonOpaque)) ==
            (GraphicsRayTracingInstanceFlags.ForceOpaque | GraphicsRayTracingInstanceFlags.ForceNonOpaque))
            throw new ArgumentOutOfRangeException(nameof(Flags));
        Transform.Validate();
    }
}

/// <summary>Build description for a bottom- or top-level acceleration structure.</summary>
public sealed record GraphicsAccelerationStructureDescription
{
    public required bool IsTopLevel { get; init; }
    public required GraphicsAccelerationStructureBuildFlags Flags { get; init; }
    public ReadOnlyMemory<GraphicsAccelerationStructureGeometry> Geometries { get; init; }
    public uint MaximumInstanceCount { get; init; }

    public void Validate() => ValidateCore(null);

    public void Validate(IGraphicsDevice expectedDevice)
    {
        ArgumentNullException.ThrowIfNull(expectedDevice);
        ValidateCore(expectedDevice);
    }

    private void ValidateCore(IGraphicsDevice? expectedDevice)
    {
        if (expectedDevice?.IsDisposed == true)
            throw new ObjectDisposedException(nameof(expectedDevice));
        const GraphicsAccelerationStructureBuildFlags allFlags =
            GraphicsAccelerationStructureBuildFlags.AllowUpdate |
            GraphicsAccelerationStructureBuildFlags.AllowCompaction |
            GraphicsAccelerationStructureBuildFlags.PreferFastTrace |
            GraphicsAccelerationStructureBuildFlags.PreferFastBuild |
            GraphicsAccelerationStructureBuildFlags.MinimizeMemory;
        if ((Flags & ~allFlags) != 0)
            throw new ArgumentOutOfRangeException(nameof(Flags));
        if (IsTopLevel)
        {
            if (MaximumInstanceCount == 0 || !Geometries.IsEmpty)
                throw new ArgumentException("Top-level structures require a non-zero instance capacity and no bottom-level geometry.");
        }
        else
        {
            if (Geometries.IsEmpty || MaximumInstanceCount != 0)
                throw new ArgumentException("Bottom-level structures require geometry and cannot declare an instance capacity.");
            IGraphicsDevice? geometryDevice = null;
            ReadOnlySpan<GraphicsAccelerationStructureGeometry> geometries = Geometries.Span;
            for (int i = 0; i < geometries.Length; i++)
            {
                geometries[i].Validate();
                if (geometryDevice is null)
                    geometryDevice = geometries[i].Device;
                else if (!ReferenceEquals(geometryDevice, geometries[i].Device))
                    throw new ArgumentException("All bottom-level geometry buffers must belong to the same device.");
                if (expectedDevice is not null && !ReferenceEquals(expectedDevice, geometries[i].Device))
                    throw new ArgumentException("Bottom-level geometry buffers must belong to the acceleration-structure device.");
            }
        }
    }

    public void ValidateInstances(IGraphicsDevice device, ReadOnlySpan<GraphicsRayTracingInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsDisposed)
            throw new ObjectDisposedException(nameof(device));
        if (!IsTopLevel)
            throw new InvalidOperationException("Only a top-level acceleration structure accepts instance data.");
        if (instances.Length > MaximumInstanceCount)
            throw new ArgumentOutOfRangeException(nameof(instances), "Instance count exceeds the structure's declared maximum.");
        for (int i = 0; i < instances.Length; i++)
            instances[i].Validate(device);
    }
}

/// <summary>Opaque acceleration structure resource. Build storage and backing memory remain owned by the implementation.</summary>
public interface IGraphicsAccelerationStructure : IGraphicsResource
{
    GraphicsAccelerationStructureDescription Description { get; }
    bool IsTopLevel { get; }

    /// <summary>Current built TLAS instance count; zero for BLAS resources or an unbuilt TLAS.</summary>
    uint InstanceCount { get; }

    ulong DeviceAddress { get; }
}

/// <summary>Optional device contract for acceleration-structure creation and size queries.</summary>
public interface IGraphicsRayTracingDevice
{
    IGraphicsDevice Device { get; }

    /// <summary>Implementations validate with <c>description.Validate(Device)</c> before querying sizes.</summary>
    GraphicsAccelerationStructureSizes GetAccelerationStructureSizes(in GraphicsAccelerationStructureDescription description);

    /// <summary>Implementations validate with <c>description.Validate(Device)</c> before creating the structure.</summary>
    IGraphicsAccelerationStructure CreateAccelerationStructure(in GraphicsAccelerationStructureDescription description);
}

public readonly record struct GraphicsAccelerationStructureSizes(
    ulong AccelerationStructureSize,
    ulong BuildScratchSize,
    ulong UpdateScratchSize,
    ulong Alignment);

/// <summary>Shader table region expressed in bytes. Empty regions use zero address, size and stride.</summary>
public readonly record struct GraphicsShaderTableRegion(ulong DeviceAddress, ulong SizeInBytes, ulong StrideInBytes)
{
    public void Validate()
    {
        if (SizeInBytes == 0)
        {
            if (DeviceAddress != 0 || StrideInBytes != 0)
                throw new ArgumentException("An empty shader table region must have a zero address and stride.");
            return;
        }
        if (DeviceAddress == 0 || StrideInBytes == 0 || SizeInBytes % StrideInBytes != 0)
            throw new ArgumentException("A non-empty shader table region requires a non-zero address and stride, and an integral record count.");
    }

    public void Validate(in GraphicsShaderBindingTableProperties properties)
    {
        Validate();
        properties.Validate();
        if (SizeInBytes == 0)
            return;
        if (DeviceAddress % properties.ShaderGroupBaseAlignment != 0)
            throw new ArgumentException("Shader table region addresses must satisfy shader-group base alignment.", nameof(DeviceAddress));
        if (StrideInBytes < properties.ShaderGroupHandleSize ||
            StrideInBytes > properties.MaximumShaderGroupStride ||
            StrideInBytes % properties.ShaderGroupHandleAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(StrideInBytes));
    }
}

/// <summary>Ray dispatch dimensions and shader-table regions.</summary>
public readonly record struct GraphicsRayDispatchDescription(
    GraphicsShaderTableRegion RayGeneration,
    GraphicsShaderTableRegion Miss,
    GraphicsShaderTableRegion Hit,
    GraphicsShaderTableRegion Callable,
    uint Width,
    uint Height,
    uint Depth)
{
    public void Validate()
    {
        if (Width == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Ray dispatch width must be non-zero.");
        if (Height == 0)
            throw new ArgumentOutOfRangeException(nameof(Height), "Ray dispatch height must be non-zero.");
        if (Depth == 0)
            throw new ArgumentOutOfRangeException(nameof(Depth), "Ray dispatch depth must be non-zero.");
        RayGeneration.Validate();
        Miss.Validate();
        Hit.Validate();
        Callable.Validate();
        if (RayGeneration.SizeInBytes == 0 || RayGeneration.StrideInBytes == 0 ||
            RayGeneration.SizeInBytes != RayGeneration.StrideInBytes)
            throw new ArgumentException("The ray-generation region must contain exactly one record.");
    }

    public void Validate(in GraphicsShaderBindingTableProperties properties)
    {
        Validate();
        RayGeneration.Validate(properties);
        Miss.Validate(properties);
        Hit.Validate(properties);
        Callable.Validate(properties);
    }
}

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

public enum GraphicsAccelerationStructureCopyMode
{
    Clone,
    Compact
}

/// <summary>
/// Optional command-list extension for building, compacting, and dispatching ray-tracing structures.
/// TLAS instance data is supplied at build time; BLAS builds pass an empty instance span.
/// </summary>

/// <summary>
/// Shared validation helpers for ray-tracing command-list implementations. Backends should call these
/// before recording build, compacted-size query, and copy commands.
/// </summary>
public static class GraphicsRayTracingCommandValidation
{
    public static void ValidateBuild(
        IGraphicsAccelerationStructure destination,
        IGraphicsAccelerationStructure? source,
        IGraphicsBuffer scratchBuffer,
        ulong scratchOffset,
        GraphicsAccelerationStructureBuildFlags flags,
        ReadOnlySpan<GraphicsRayTracingInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(scratchBuffer);
        if (destination.IsDisposed || destination.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(destination));
        if (scratchBuffer.IsDisposed || scratchBuffer.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(scratchBuffer));
        if (!ReferenceEquals(destination.Device, scratchBuffer.Device))
            throw new ArgumentException("The destination structure and scratch buffer must belong to the same device.", nameof(scratchBuffer));
        if ((scratchBuffer.Description.Usage & GraphicsBufferUsage.Storage) == 0)
            throw new ArgumentException("Acceleration-structure scratch buffers must declare Storage usage.", nameof(scratchBuffer));
        if (scratchOffset >= scratchBuffer.Description.SizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(scratchOffset));

        const GraphicsAccelerationStructureBuildFlags allFlags =
            GraphicsAccelerationStructureBuildFlags.AllowUpdate |
            GraphicsAccelerationStructureBuildFlags.AllowCompaction |
            GraphicsAccelerationStructureBuildFlags.PreferFastTrace |
            GraphicsAccelerationStructureBuildFlags.PreferFastBuild |
            GraphicsAccelerationStructureBuildFlags.MinimizeMemory;
        if ((flags & ~allFlags) != 0)
            throw new ArgumentOutOfRangeException(nameof(flags));
        if (destination.IsTopLevel != destination.Description.IsTopLevel)
            throw new ArgumentException("The destination structure kind does not match its description.", nameof(destination));

        if (destination.IsTopLevel)
            destination.Description.ValidateInstances(destination.Device, instances);
        else if (!instances.IsEmpty)
            throw new ArgumentException("BLAS builds must not provide TLAS instance data.", nameof(instances));

        if (source is null)
            return;

        if (source.IsDisposed || source.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(source));
        if (!ReferenceEquals(source.Device, destination.Device))
            throw new ArgumentException("Source and destination acceleration structures must belong to the same device.", nameof(source));
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("An acceleration structure cannot be updated in place using itself as the source.", nameof(source));
        if (source.IsTopLevel != destination.IsTopLevel)
            throw new ArgumentException("Source and destination acceleration structures must have the same kind.", nameof(source));
        if ((source.Description.Flags & GraphicsAccelerationStructureBuildFlags.AllowUpdate) == 0)
            throw new ArgumentException("The source acceleration structure was not created with AllowUpdate.", nameof(source));
        if (source.IsTopLevel && source.InstanceCount != (uint)instances.Length)
            throw new ArgumentException("A TLAS update must preserve the source instance count.", nameof(instances));
    }

    public static void ValidateCompactedSizeWrite(
        IGraphicsAccelerationStructure accelerationStructure,
        IGraphicsQueryPool queryPool,
        uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(accelerationStructure);
        ArgumentNullException.ThrowIfNull(queryPool);
        if (accelerationStructure.IsDisposed || accelerationStructure.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(accelerationStructure));
        if (queryPool.IsDisposed || queryPool.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(queryPool));
        if (!ReferenceEquals(accelerationStructure.Device, queryPool.Device))
            throw new ArgumentException("The acceleration structure and query pool must belong to the same device.", nameof(queryPool));
        if ((accelerationStructure.Description.Flags & GraphicsAccelerationStructureBuildFlags.AllowCompaction) == 0)
            throw new ArgumentException("The acceleration structure was not created with AllowCompaction.", nameof(accelerationStructure));
        if (queryPool.Description.Type != GraphicsQueryType.AccelerationStructureCompactedSize)
            throw new ArgumentException("Compacted-size writes require an acceleration-structure compacted-size query pool.", nameof(queryPool));
        if (queryIndex >= queryPool.Description.QueryCount)
            throw new ArgumentOutOfRangeException(nameof(queryIndex));
    }

    public static void ValidateCopy(
        IGraphicsAccelerationStructure source,
        IGraphicsAccelerationStructure destination,
        GraphicsAccelerationStructureCopyMode mode)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.IsDisposed || source.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(source));
        if (destination.IsDisposed || destination.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(destination));
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!ReferenceEquals(source.Device, destination.Device))
            throw new ArgumentException("Source and destination acceleration structures must belong to the same device.");
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("An acceleration structure cannot be copied onto itself.", nameof(destination));
        if (source.IsTopLevel != destination.IsTopLevel)
            throw new ArgumentException("Source and destination acceleration structures must have the same kind.", nameof(destination));
        if (mode == GraphicsAccelerationStructureCopyMode.Compact &&
            (source.Description.Flags & GraphicsAccelerationStructureBuildFlags.AllowCompaction) == 0)
            throw new ArgumentException("The source acceleration structure was not created with AllowCompaction.", nameof(source));
    }
}

public interface IGraphicsRayTracingCommandList : IGraphicsCommandList
{
    void BuildAccelerationStructure(
        IGraphicsAccelerationStructure destination,
        IGraphicsAccelerationStructure? source,
        IGraphicsBuffer scratchBuffer,
        ulong scratchOffset,
        GraphicsAccelerationStructureBuildFlags flags,
        ReadOnlySpan<GraphicsRayTracingInstance> instances);

    void WriteAccelerationStructureCompactedSize(
        IGraphicsAccelerationStructure accelerationStructure,
        IGraphicsQueryPool queryPool,
        uint queryIndex);

    void CopyAccelerationStructure(
        IGraphicsAccelerationStructure source,
        IGraphicsAccelerationStructure destination,
        GraphicsAccelerationStructureCopyMode mode);

    void DispatchRays(IGraphicsRayTracingPipeline pipeline, in GraphicsRayDispatchDescription description);
}

/// <summary>Ray-tracing pipeline contract. Shader artifacts and group composition are supplied by the application.</summary>
public interface IGraphicsRayTracingPipeline : IGraphicsPipeline
{
    GraphicsRayTracingPipelineDescription Description { get; }
}

/// <summary>One shader artifact and its ray-tracing execution stage. Entry-point names are supplied by the shader artifact.</summary>
public readonly record struct GraphicsRayTracingShaderEntry(
    IGraphicsShader Shader,
    GraphicsRayTracingShaderStage Stage,
    string EntryPoint);

public enum GraphicsRayTracingShaderGroupType
{
    General,
    TrianglesHitGroup,
    ProceduralHitGroup
}

/// <summary>
/// Shader-group composition for a ray-tracing pipeline. Shader indices refer to the Stages array;
/// -1 means that a hit-group stage is absent.
/// </summary>
public readonly record struct GraphicsRayTracingShaderGroup(
    GraphicsRayTracingShaderGroupType Type,
    int GeneralShaderIndex,
    int ClosestHitShaderIndex,
    int AnyHitShaderIndex,
    int IntersectionShaderIndex);

/// <summary>Ray tracing pipeline stage and shader-group description; no shader compiler is implied.</summary>
public sealed record GraphicsRayTracingPipelineDescription
{
    public required IGraphicsPipelineLayout Layout { get; init; }
    public required ReadOnlyMemory<GraphicsRayTracingShaderEntry> Stages { get; init; }
    public required ReadOnlyMemory<GraphicsRayTracingShaderGroup> Groups { get; init; }
    public required uint MaximumRecursionDepth { get; init; }
    public required uint MaximumPayloadSizeInBytes { get; init; }
    public required uint MaximumAttributeSizeInBytes { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Layout);
        if (Stages.IsEmpty || Groups.IsEmpty)
            throw new ArgumentException("A ray-tracing pipeline requires shader stages and shader groups.");
        if (MaximumRecursionDepth == 0) throw new ArgumentOutOfRangeException(nameof(MaximumRecursionDepth));

        ReadOnlySpan<GraphicsRayTracingShaderEntry> stages = Stages.Span;
        for (int i = 0; i < stages.Length; i++)
        {
            if (stages[i].Shader is null || !Enum.IsDefined(stages[i].Stage) ||
                string.IsNullOrWhiteSpace(stages[i].EntryPoint))
                throw new ArgumentException($"Ray-tracing shader stage {i} is invalid.");
        }

        ReadOnlySpan<GraphicsRayTracingShaderGroup> groups = Groups.Span;
        for (int i = 0; i < groups.Length; i++)
        {
            GraphicsRayTracingShaderGroup group = groups[i];
            if (!Enum.IsDefined(group.Type))
                throw new ArgumentOutOfRangeException(nameof(Groups));

            if (group.Type == GraphicsRayTracingShaderGroupType.General)
            {
                ValidateShaderIndex(group.GeneralShaderIndex, stages.Length, nameof(Groups));
                if (group.ClosestHitShaderIndex != -1 || group.AnyHitShaderIndex != -1 ||
                    group.IntersectionShaderIndex != -1)
                    throw new ArgumentException("General shader groups cannot contain hit-group shader indices.");
                if (stages[group.GeneralShaderIndex].Stage is not
                    (GraphicsRayTracingShaderStage.RayGeneration or GraphicsRayTracingShaderStage.Miss or GraphicsRayTracingShaderStage.Callable))
                    throw new ArgumentException("General groups require ray-generation, miss, or callable shaders.");
            }
            else
            {
                if (group.GeneralShaderIndex != -1)
                    throw new ArgumentException("Hit groups cannot specify a general shader.");
                ValidateOptionalShaderIndex(group.ClosestHitShaderIndex, stages.Length, GraphicsRayTracingShaderStage.ClosestHit, stages);
                ValidateOptionalShaderIndex(group.AnyHitShaderIndex, stages.Length, GraphicsRayTracingShaderStage.AnyHit, stages);
                ValidateOptionalShaderIndex(group.IntersectionShaderIndex, stages.Length, GraphicsRayTracingShaderStage.Intersection, stages);
                if (group.Type == GraphicsRayTracingShaderGroupType.TrianglesHitGroup && group.IntersectionShaderIndex != -1)
                    throw new ArgumentException("Triangle hit groups cannot specify an intersection shader.");
                if (group.Type == GraphicsRayTracingShaderGroupType.ProceduralHitGroup && group.IntersectionShaderIndex < 0)
                    throw new ArgumentException("Procedural hit groups require an intersection shader.");
                if (group.ClosestHitShaderIndex == -1 && group.AnyHitShaderIndex == -1)
                    throw new ArgumentException("A hit group must contain a closest-hit or any-hit shader.");
            }
        }
    }

    private static void ValidateShaderIndex(int index, int count, string parameterName)
    {
        if ((uint)index >= (uint)count)
            throw new ArgumentOutOfRangeException(parameterName, "Shader group index is outside the shader stage array.");
    }

    private static void ValidateOptionalShaderIndex(
        int index, int count, GraphicsRayTracingShaderStage expected,
        ReadOnlySpan<GraphicsRayTracingShaderEntry> stages)
    {
        if (index == -1) return;
        ValidateShaderIndex(index, count, nameof(Stages));
        if (stages[index].Stage != expected)
            throw new ArgumentException($"Shader index {index} must reference the {expected} stage.");
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

/// <summary>
/// Query commands are independent of dynamic rendering. Timestamp queries may be supported on graphics,
/// compute, or copy queues; occlusion and pipeline-statistics queries require graphics queue support.
/// Implementations validate pool type, index, and queue legality.
/// </summary>
public interface IGraphicsQueryCommandList : IGraphicsCommandList
{
    void BeginQuery(IGraphicsQueryPool queryPool, uint queryIndex);
    void EndQuery(IGraphicsQueryPool queryPool, uint queryIndex);
    void WriteTimestamp(IGraphicsQueryPool queryPool, uint queryIndex);
}

/// <summary>GPU marker scopes are diagnostic annotations and must not alter execution ordering.</summary>
public interface IGraphicsCommandDiagnostics
{
    void BeginLabel(string name, uint color = 0);
    void EndLabel();
    void InsertMarker(string name, uint color = 0);
}
