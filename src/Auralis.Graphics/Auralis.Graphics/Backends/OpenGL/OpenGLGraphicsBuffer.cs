using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

internal interface IOpenGLNativeResource
{
    void ReleaseNative(GL gl);
}

/// <summary>OpenGL buffer backed by driver storage and invalidatable managed staging memory for Map/Unmap.</summary>
internal sealed unsafe class OpenGLGraphicsBuffer : GraphicsDeviceObject, IGraphicsBuffer, IOpenGLNativeResource
{
    private readonly OpenGLGraphicsDevice _device;
    private uint _handle;
    private OpenGLMappedMemoryManager? _mapped;
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
        if (Volatile.Read(ref _mapped) is not null)
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
        OpenGLMappedMemoryManager manager = new(staging);
        if (Interlocked.CompareExchange(ref _mapped, manager, null) is not null)
        {
            manager.Invalidate();
            throw new InvalidOperationException("The buffer is already mapped.");
        }

        _mappedOffset = offset;
        _mappedMode = mode;
        try
        {
            if (mode is GraphicsMapMode.Read or GraphicsMapMode.ReadWrite)
            {
                _device.WithContext(gl =>
                {
                    fixed (byte* data = staging)
                        _device.DirectStateAccess.GetNamedBufferSubData(
                            _handle, (nint)offset, (nuint)size, data);
                });
            }

            ThrowIfDisposed();
            return manager.Memory;
        }
        catch
        {
            Interlocked.CompareExchange(ref _mapped, null, manager);
            manager.Invalidate();
            _mappedOffset = 0;
            throw;
        }
    }

    public void Unmap()
    {
        ThrowIfDisposed();
        OpenGLMappedMemoryManager? manager = Volatile.Read(ref _mapped);
        if (manager is null)
            throw new InvalidOperationException("The buffer is not mapped.");

        if (_mappedMode is GraphicsMapMode.Write or GraphicsMapMode.ReadWrite)
        {
            byte[] staging = manager.GetBuffer();
            ulong offset = _mappedOffset;
            _device.WithContext(gl =>
            {
                fixed (byte* data = staging)
                    _device.DirectStateAccess.NamedBufferSubData(
                        _handle, (nint)offset, (nuint)staging.Length, data);
            });
        }

        if (ReferenceEquals(Interlocked.CompareExchange(ref _mapped, null, manager), manager))
            manager.Invalidate();
        _mappedOffset = 0;
    }

    protected override void DisposeCore()
    {
        OpenGLMappedMemoryManager? mapped = Interlocked.Exchange(ref _mapped, null);
        mapped?.Invalidate();
        _mappedOffset = 0;
        _device.ReleaseResource(this);
    }

    public void ReleaseNative(GL gl)
    {
        OpenGLMappedMemoryManager? mapped = Interlocked.Exchange(ref _mapped, null);
        mapped?.Invalidate();
        _mappedOffset = 0;

        uint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            gl.DeleteBuffer(handle);
    }
}
