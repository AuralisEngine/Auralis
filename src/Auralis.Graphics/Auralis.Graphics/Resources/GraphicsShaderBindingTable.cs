using Auralis.Graphics.Devices;

namespace Auralis.Graphics.Resources;

/// <summary>Optional buffer capability exposing a backend-neutral GPU virtual address.</summary>
public interface IGraphicsBufferDeviceAddress
{
    ulong DeviceAddress { get; }
}

/// <summary>Hardware alignment and handle-size requirements for shader binding tables.</summary>
public readonly record struct GraphicsShaderBindingTableProperties(
    uint ShaderGroupHandleSize,
    uint ShaderGroupHandleAlignment,
    uint ShaderGroupBaseAlignment,
    uint MaximumShaderGroupStride)
{
    public void Validate()
    {
        if (ShaderGroupHandleSize == 0 || !IsPowerOfTwo(ShaderGroupHandleAlignment) ||
            !IsPowerOfTwo(ShaderGroupBaseAlignment) ||
            ShaderGroupBaseAlignment < ShaderGroupHandleAlignment ||
            ShaderGroupBaseAlignment % ShaderGroupHandleAlignment != 0 ||
            MaximumShaderGroupStride < ShaderGroupHandleSize)
            throw new ArgumentException("Shader binding table properties contain invalid size or alignment values.");
    }

    internal static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
}

/// <summary>One shader binding table region backed by a device-addressable buffer.</summary>
public readonly record struct GraphicsShaderBindingTableRegion(
    IGraphicsBuffer? Buffer,
    ulong OffsetInBytes,
    ulong SizeInBytes,
    uint StrideInBytes)
{
    public ulong DeviceAddress
    {
        get
        {
            if (Buffer is not IGraphicsBufferDeviceAddress address)
                throw new NotSupportedException("The buffer does not expose a GPU device address.");
            return checked(address.DeviceAddress + OffsetInBytes);
        }
    }

    public void Validate(in GraphicsShaderBindingTableProperties properties)
    {
        properties.Validate();
        if (SizeInBytes == 0)
        {
            if (Buffer is not null || OffsetInBytes != 0 || StrideInBytes != 0)
                throw new ArgumentException("An empty shader table region must not reference a buffer and must have zero offset and stride.");
            return;
        }
        IGraphicsBuffer buffer = Buffer ?? throw new ArgumentNullException(nameof(Buffer));
        if (buffer.IsDisposed || buffer.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(Buffer));
        if (buffer is not IGraphicsBufferDeviceAddress address)
            throw new ArgumentException("Shader binding table buffers must expose a GPU device address.");
        if ((buffer.Description.Usage & GraphicsBufferUsage.ShaderBindingTable) == 0)
            throw new ArgumentException("Shader binding table buffers must declare ShaderBindingTable usage.");
        if (StrideInBytes < properties.ShaderGroupHandleSize ||
            StrideInBytes > properties.MaximumShaderGroupStride ||
            StrideInBytes % properties.ShaderGroupHandleAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(StrideInBytes));
        if (OffsetInBytes % properties.ShaderGroupBaseAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(OffsetInBytes));
        if (OffsetInBytes > buffer.Description.SizeInBytes ||
            SizeInBytes > buffer.Description.SizeInBytes - OffsetInBytes ||
            SizeInBytes % StrideInBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes));
        ulong deviceAddress = checked(address.DeviceAddress + OffsetInBytes);
        if (deviceAddress == 0)
            throw new ArgumentException("A non-empty shader table region must have a non-zero GPU address.");
        if (deviceAddress % properties.ShaderGroupBaseAlignment != 0)
            throw new ArgumentException("The resulting GPU address does not satisfy shader-group base alignment.");
    }
}

/// <summary>Optional ray-tracing extension for shader group handles and SBT alignment metadata.</summary>
public interface IGraphicsRayTracingShaderTableDevice : IGraphicsRayTracingPipelineDevice
{
    GraphicsShaderBindingTableProperties ShaderBindingTableProperties { get; }

    /// <summary>
    /// Writes contiguous opaque handles beginning at firstGroup. Destination length must be an exact
    /// multiple of ShaderGroupHandleSize; handles are written in increasing shader-group index order.
    /// </summary>
    void GetShaderGroupHandles(
        IGraphicsRayTracingPipeline pipeline,
        uint firstGroup,
        Span<byte> destination);
}

/// <summary>Complete set of shader binding table regions for a ray dispatch.</summary>
public readonly record struct GraphicsShaderBindingTable(
    GraphicsShaderBindingTableRegion RayGeneration,
    GraphicsShaderBindingTableRegion Miss,
    GraphicsShaderBindingTableRegion Hit,
    GraphicsShaderBindingTableRegion Callable)
{
    public GraphicsRayDispatchDescription ToDispatchDescription(
        in GraphicsShaderBindingTableProperties properties,
        uint width,
        uint height,
        uint depth = 1)
    {
        Validate(properties);
        if (width == 0 || height == 0 || depth == 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        return new GraphicsRayDispatchDescription(
            ToRegion(RayGeneration), ToRegion(Miss), ToRegion(Hit), ToRegion(Callable), width, height, depth);
    }

    private static GraphicsShaderTableRegion ToRegion(GraphicsShaderBindingTableRegion region) =>
        region.SizeInBytes == 0
            ? default
            : new GraphicsShaderTableRegion(region.DeviceAddress, region.SizeInBytes, region.StrideInBytes);

    public void Validate(in GraphicsShaderBindingTableProperties properties)
    {
        RayGeneration.Validate(properties);
        Miss.Validate(properties);
        Hit.Validate(properties);
        Callable.Validate(properties);
        if (RayGeneration.SizeInBytes == 0 || RayGeneration.StrideInBytes == 0 ||
            RayGeneration.SizeInBytes != RayGeneration.StrideInBytes)
            throw new ArgumentException("The ray-generation region must contain exactly one record.");
    }
}


/// <summary>One shader binding table record: a shader-group index followed by application-defined local data.</summary>
public readonly record struct GraphicsShaderBindingTableRecord(
    uint ShaderGroupIndex,
    ReadOnlyMemory<byte> LocalData);

/// <summary>Computed record stride and total storage required for a shader binding table region.</summary>
public readonly record struct GraphicsShaderBindingTableLayout(uint StrideInBytes, ulong SizeInBytes);

/// <summary>
/// Allocation-free record packer. The caller supplies destination storage and handle scratch space;
/// GPU buffer allocation and mapping remain the responsibility of the application/backend.
/// </summary>
public static class GraphicsShaderBindingTableWriter
{
    public static GraphicsShaderBindingTableLayout GetLayout(
        ReadOnlySpan<GraphicsShaderBindingTableRecord> records,
        in GraphicsShaderBindingTableProperties properties)
    {
        properties.Validate();
        if (records.IsEmpty)
            throw new ArgumentException("A shader binding table region must contain at least one record.", nameof(records));

        uint maximumLocalDataSize = 0;
        for (int i = 0; i < records.Length; i++)
        {
            maximumLocalDataSize = Math.Max(maximumLocalDataSize, (uint)records[i].LocalData.Length);
        }

        ulong unalignedStride = (ulong)properties.ShaderGroupHandleSize + maximumLocalDataSize;
        ulong alignment = properties.ShaderGroupHandleAlignment;
        ulong stride = ((unalignedStride + alignment - 1) / alignment) * alignment;
        if (stride > uint.MaxValue || stride > properties.MaximumShaderGroupStride)
            throw new ArgumentOutOfRangeException(nameof(records),
                "The shader binding table record exceeds the device's maximum stride.");

        uint strideInBytes = (uint)stride;
        return new GraphicsShaderBindingTableLayout(
            strideInBytes,
            stride * (uint)records.Length);
    }

    public static GraphicsShaderBindingTableLayout WriteRecords(
        IGraphicsRayTracingShaderTableDevice device,
        IGraphicsRayTracingPipeline pipeline,
        ReadOnlySpan<GraphicsShaderBindingTableRecord> records,
        Span<byte> destination,
        Span<byte> handleScratch)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (device.Device.IsDisposed || pipeline.IsDisposed || pipeline.Device.IsDisposed)
            throw new ObjectDisposedException(nameof(pipeline));
        if (!ReferenceEquals(pipeline.Device, device.Device))
            throw new ArgumentException("The pipeline and shader-table device must belong to the same graphics device.");

        GraphicsShaderBindingTableProperties properties = device.ShaderBindingTableProperties;
        properties.Validate();
        GraphicsShaderBindingTableLayout layout = GetLayout(records, properties);
        if ((ulong)destination.Length < layout.SizeInBytes)
            throw new ArgumentException("Destination storage is too small for the shader binding table.", nameof(destination));

        int handleSize = checked((int)properties.ShaderGroupHandleSize);
        int stride = checked((int)layout.StrideInBytes);
        for (int i = 0; i < records.Length; i++)
        {
            if (records[i].ShaderGroupIndex >= pipeline.Description.Groups.Length)
                throw new ArgumentOutOfRangeException(nameof(records), "A record references a shader group outside the pipeline.");
        }

        uint minimumGroupIndex = uint.MaxValue;
        uint maximumGroupIndex = 0;
        for (int i = 0; i < records.Length; i++)
        {
            uint groupIndex = records[i].ShaderGroupIndex;
            minimumGroupIndex = Math.Min(minimumGroupIndex, groupIndex);
            maximumGroupIndex = Math.Max(maximumGroupIndex, groupIndex);
        }

        int groupCount = checked((int)(maximumGroupIndex - minimumGroupIndex + 1));
        int handlesSize = checked(handleSize * groupCount);
        if (handleScratch.Length < handlesSize)
            throw new ArgumentException("Handle scratch storage must hold handles from the lowest through highest referenced shader group.", nameof(handleScratch));
        Span<byte> handles = handleScratch[..handlesSize];
        Span<byte> destinationSpan = destination[..checked((int)layout.SizeInBytes)];
        if (handles.Overlaps(destinationSpan))
            throw new ArgumentException("Handle scratch storage and destination storage must not overlap.");
        for (int i = 0; i < records.Length; i++)
        {
            if (records[i].LocalData.Span.Overlaps(destinationSpan) ||
                records[i].LocalData.Span.Overlaps(handles))
                throw new ArgumentException("Record local data must not overlap the destination or handle scratch storage.", nameof(records));
        }

        device.GetShaderGroupHandles(pipeline, minimumGroupIndex, handles);
        for (int i = 0; i < records.Length; i++)
        {
            GraphicsShaderBindingTableRecord record = records[i];
            Span<byte> destinationRecord = destination.Slice(checked(i * stride), stride);
            destinationRecord.Clear();
            int handleOffset = checked((int)(record.ShaderGroupIndex - minimumGroupIndex) * handleSize);
            handles.Slice(handleOffset, handleSize).CopyTo(destinationRecord);
            record.LocalData.Span.CopyTo(destinationRecord[handleSize..]);
        }

        return layout;
    }
}
