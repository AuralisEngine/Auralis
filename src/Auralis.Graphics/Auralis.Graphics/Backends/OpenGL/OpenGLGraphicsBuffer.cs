using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

internal interface IOpenGLNativeResource
{
    void ReleaseNative(GL gl);
}

/// <summary>OpenGL buffer backed by driver storage and managed staging memory for Map/Unmap.</summary>
internal sealed unsafe class OpenGLGraphicsBuffer : GraphicsDeviceObject, IGraphicsBuffer, IOpenGLNativeResource
{
    private readonly OpenGLGraphicsDevice _device;
    private uint _handle;
    private byte[]? _mapped;
    private ulong _mappedOffset;
    private GraphicsMapMode _mappedMode;

    internal OpenGLGraphicsBuffer(OpenGLGraphicsDevice device, in GraphicsBufferDescription description, uint handle)
        : base(device)
    {
        _device = device;
        Description = description;
        _handle = handle;
    }

    public GraphicsBufferDescription Description { get; }

    public string? DebugName => null;

    internal uint Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (_mapped is not null)
            throw new InvalidOperationException("The buffer is already mapped.");

        bool canRead = Description.MemoryAccess is GraphicsMemoryAccess.GpuToCpu or GraphicsMemoryAccess.CpuReadWrite;
        bool canWrite = Description.MemoryAccess is GraphicsMemoryAccess.CpuToGpu or GraphicsMemoryAccess.CpuReadWrite;
        if ((mode is GraphicsMapMode.Read or GraphicsMapMode.ReadWrite) && !canRead)
            throw new GraphicsValidationException("The buffer was not created with CPU-readable memory access.");
        if ((mode is GraphicsMapMode.Write or GraphicsMapMode.ReadWrite) && !canWrite)
            throw new GraphicsValidationException("The buffer was not created with CPU-writable memory access.");

        if (size == ulong.MaxValue)
            size = Description.SizeInBytes - Math.Min(offset, Description.SizeInBytes);
        if (offset > Description.SizeInBytes || size == 0 || size > Description.SizeInBytes - offset ||
            size > int.MaxValue || offset > (ulong)nint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(size), "The mapped range is empty or outside the buffer.");

        byte[] staging = GC.AllocateUninitializedArray<byte>((int)size);
        if (mode is GraphicsMapMode.Read or GraphicsMapMode.ReadWrite)
        {
            _device.WithContext(gl =>
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, _handle);
                fixed (byte* data = staging)
                    gl.GetBufferSubData(BufferTargetARB.ArrayBuffer, (nint)offset, (nuint)size, data);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            });
        }

        _mapped = staging;
        _mappedOffset = offset;
        _mappedMode = mode;
        return staging;
    }

    public void Unmap()
    {
        ThrowIfDisposed();
        byte[]? staging = _mapped;
        if (staging is null)
            throw new InvalidOperationException("The buffer is not mapped.");

        if (_mappedMode is GraphicsMapMode.Write or GraphicsMapMode.ReadWrite)
        {
            ulong offset = _mappedOffset;
            _device.WithContext(gl =>
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, _handle);
                fixed (byte* data = staging)
                    gl.BufferSubData(BufferTargetARB.ArrayBuffer, (nint)offset, (nuint)staging.Length, data);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            });
        }

        _mapped = null;
        _mappedOffset = 0;
    }

    protected override void DisposeCore()
    {
        if (_mapped is not null)
        {
            _mapped = null;
            _mappedOffset = 0;
        }

        _device.WithContext(ReleaseNative);
    }

    public void ReleaseNative(GL gl)
    {
        uint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            gl.DeleteBuffer(handle);
    }
}
