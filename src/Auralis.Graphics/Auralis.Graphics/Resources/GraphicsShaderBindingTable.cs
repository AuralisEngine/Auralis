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
            !IsPowerOfTwo(ShaderGroupBaseAlignment) || MaximumShaderGroupStride < ShaderGroupHandleSize)
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
        if (deviceAddress % properties.ShaderGroupBaseAlignment != 0)
            throw new ArgumentException("The resulting GPU address does not satisfy shader-group base alignment.");
    }
}

/// <summary>Optional ray-tracing extension for shader group handles and SBT alignment metadata.</summary>
public interface IGraphicsRayTracingShaderTableDevice : IGraphicsRayTracingPipelineDevice
{
    GraphicsShaderBindingTableProperties ShaderBindingTableProperties { get; }
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
