using Auralis.Graphics.Devices;
using System.Runtime.InteropServices;

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
            IGraphicsBuffer buffer = Buffer ??
                throw new InvalidOperationException("An empty shader table region has no GPU device address.");
            return GraphicsBufferAddressRangeValidation.Validate(
                buffer, null, OffsetInBytes, SizeInBytes, 1,
                GraphicsBufferUsage.ShaderBindingTable, nameof(Buffer));
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
        if (StrideInBytes < properties.ShaderGroupHandleSize ||
            StrideInBytes > properties.MaximumShaderGroupStride ||
            StrideInBytes % properties.ShaderGroupHandleAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(StrideInBytes));
        if (SizeInBytes % StrideInBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(SizeInBytes));
        _ = GraphicsBufferAddressRangeValidation.Validate(
            buffer, null, OffsetInBytes, SizeInBytes, properties.ShaderGroupBaseAlignment,
            GraphicsBufferUsage.ShaderBindingTable, nameof(Buffer));
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

        IGraphicsDevice? commonDevice = null;
        ValidateRegionDevice(RayGeneration, ref commonDevice);
        ValidateRegionDevice(Miss, ref commonDevice);
        ValidateRegionDevice(Hit, ref commonDevice);
        ValidateRegionDevice(Callable, ref commonDevice);

        if (RayGeneration.SizeInBytes == 0 || RayGeneration.StrideInBytes == 0 ||
            RayGeneration.SizeInBytes != RayGeneration.StrideInBytes)
            throw new ArgumentException("The ray-generation region must contain exactly one record.");
    }

    private static void ValidateRegionDevice(
        in GraphicsShaderBindingTableRegion region,
        ref IGraphicsDevice? commonDevice)
    {
        if (region.Buffer is not { } buffer)
            return;
        if (commonDevice is null)
            commonDevice = buffer.Device;
        else if (!ReferenceEquals(commonDevice, buffer.Device))
            throw new ArgumentException("All non-empty shader binding table regions must belong to the same graphics device.");
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
    /// <summary>
    /// Returns the maximum handle-storage size for a record count. The writer may use less when
    /// multiple records reference the same shader group.
    /// </summary>
    public static int GetHandleScratchSizeUpperBound(
        int recordCount,
        in GraphicsShaderBindingTableProperties properties)
    {
        properties.Validate();
        if (recordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(recordCount));

        ulong size = (ulong)recordCount * properties.ShaderGroupHandleSize;
        if (size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordCount),
                "The shader-group handle scratch size exceeds the maximum span length.");
        return (int)size;
    }

    /// <summary>Computes aligned record layout without allocating temporary storage.</summary>
    public static GraphicsShaderBindingTableLayout GetLayout(
        ReadOnlySpan<GraphicsShaderBindingTableRecord> records,
        in GraphicsShaderBindingTableProperties properties)
    {
        properties.Validate();
        if (records.IsEmpty)
            throw new ArgumentException("A shader binding table region must contain at least one record.", nameof(records));

        uint maximumLocalDataSize = 0;
        for (int i = 0; i < records.Length; i++)
            maximumLocalDataSize = Math.Max(maximumLocalDataSize, (uint)records[i].LocalData.Length);

        ulong unalignedStride = (ulong)properties.ShaderGroupHandleSize + maximumLocalDataSize;
        ulong alignment = properties.ShaderGroupHandleAlignment;
        ulong stride = ((unalignedStride + alignment - 1) / alignment) * alignment;
        if (stride > uint.MaxValue || stride > properties.MaximumShaderGroupStride)
            throw new ArgumentOutOfRangeException(nameof(records),
                "The shader binding table record exceeds the device's maximum stride.");

        uint strideInBytes = (uint)stride;
        return new GraphicsShaderBindingTableLayout(strideInBytes, stride * (uint)records.Length);
    }

    /// <summary>
    /// Writes shader-group handles and application data without allocating temporary storage.
    /// <paramref name="groupIndexScratch"/> must contain one index slot per record. The writer sorts
    /// and compacts this span in place to the unique referenced group indices. <paramref name="handleScratch"/>
    /// must hold one handle per distinct group (use <see cref="GetHandleScratchSizeUpperBound"/> for
    /// a conservative capacity). Only contiguous runs of referenced group indices are fetched.
    /// All scratch and destination spans, and every record's local-data span, must be non-overlapping.
    /// </summary>
    public static GraphicsShaderBindingTableLayout WriteRecords(
        IGraphicsRayTracingShaderTableDevice device,
        IGraphicsRayTracingPipeline pipeline,
        ReadOnlySpan<GraphicsShaderBindingTableRecord> records,
        Span<byte> destination,
        Span<byte> handleScratch,
        Span<uint> groupIndexScratch)
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
        if (groupIndexScratch.Length < records.Length)
            throw new ArgumentException("Group-index scratch storage must hold one index per record.", nameof(groupIndexScratch));

        int handleSize = checked((int)properties.ShaderGroupHandleSize);
        for (int i = 0; i < records.Length; i++)
        {
            if (records[i].ShaderGroupIndex >= pipeline.Description.Groups.Length)
                throw new ArgumentOutOfRangeException(nameof(records),
                    "A record references a shader group outside the pipeline.");
            groupIndexScratch[i] = records[i].ShaderGroupIndex;
        }

        Span<uint> groupIndices = groupIndexScratch[..records.Length];
        Span<byte> destinationSpan = destination[..checked((int)layout.SizeInBytes)];
        Span<byte> groupIndexBytes = MemoryMarshal.AsBytes(groupIndices);
        if (destinationSpan.Overlaps(handleScratch) ||
            destinationSpan.Overlaps(groupIndexBytes) ||
            handleScratch.Overlaps(groupIndexBytes))
            throw new ArgumentException("Group-index scratch, handle scratch, and destination storage must not overlap.");

        for (int i = 0; i < records.Length; i++)
        {
            ReadOnlySpan<byte> localData = records[i].LocalData.Span;
            if (localData.Overlaps(destinationSpan) ||
                localData.Overlaps(handleScratch) ||
                localData.Overlaps(groupIndexBytes))
                throw new ArgumentException(
                    "Record local data must not overlap destination or scratch storage.", nameof(records));
        }

        groupIndices.Sort();
        int uniqueGroupCount = 0;
        for (int i = 0; i < groupIndices.Length; i++)
        {
            uint groupIndex = groupIndices[i];
            if (uniqueGroupCount == 0 || groupIndices[uniqueGroupCount - 1] != groupIndex)
                groupIndices[uniqueGroupCount++] = groupIndex;
        }
        groupIndices = groupIndices[..uniqueGroupCount];

        int requiredHandleBytes = checked(uniqueGroupCount * handleSize);
        if (handleScratch.Length < requiredHandleBytes)
            throw new ArgumentException(
                "Handle scratch storage must hold one handle per distinct referenced shader group.",
                nameof(handleScratch));
        Span<byte> handles = handleScratch[..requiredHandleBytes];

        int runStart = 0;
        while (runStart < uniqueGroupCount)
        {
            int runEnd = runStart + 1;
            while (runEnd < uniqueGroupCount &&
                   groupIndices[runEnd - 1] != uint.MaxValue &&
                   groupIndices[runEnd] == groupIndices[runEnd - 1] + 1)
                runEnd++;

            int handleOffset = checked(runStart * handleSize);
            int runByteCount = checked((runEnd - runStart) * handleSize);
            device.GetShaderGroupHandles(
                pipeline, groupIndices[runStart], handles.Slice(handleOffset, runByteCount));
            runStart = runEnd;
        }

        int stride = checked((int)layout.StrideInBytes);
        for (int i = 0; i < records.Length; i++)
        {
            uint groupIndex = records[i].ShaderGroupIndex;
            int groupPosition = BinarySearch(groupIndices, groupIndex);
            if (groupPosition < 0)
                throw new InvalidOperationException("The shader-group index was lost while compacting scratch storage.");

            Span<byte> destinationRecord = destinationSpan.Slice(checked(i * stride), stride);
            destinationRecord.Clear();
            handles.Slice(checked(groupPosition * handleSize), handleSize).CopyTo(destinationRecord);
            records[i].LocalData.Span.CopyTo(destinationRecord[handleSize..]);
        }

        return layout;
    }

    private static int BinarySearch(ReadOnlySpan<uint> sortedValues, uint value)
    {
        int low = 0;
        int high = sortedValues.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            uint current = sortedValues[middle];
            if (current == value)
                return middle;
            if (current < value)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return -1;
    }
}