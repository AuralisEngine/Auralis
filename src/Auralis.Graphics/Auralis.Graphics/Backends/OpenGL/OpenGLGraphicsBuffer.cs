using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

internal interface IOpenGLNativeResource
{
    void ReleaseNative(GL gl);
}

/// <summary>Native OpenGL buffer with directly mapped driver memory.</summary>
internal sealed unsafe class OpenGLGraphicsBuffer : GraphicsDeviceObject, IGraphicsBuffer, IOpenGLNativeResource
{
    private readonly OpenGLGraphicsDevice _device;
    private readonly object _mappingGate = new();
    private uint _handle;
    private OpenGLMappedMemoryManager? _mapped;
    private bool _isMapped;

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

        // Context serialization is acquired before the per-buffer mapping lock. Resource release
        // follows the same lock order, preventing Map/Dispose lock inversion.
        return _device.WithContext(gl =>
        {
            lock (_mappingGate)
            {
                ThrowIfDisposed();
                if (_isMapped)
                    throw new InvalidOperationException("The buffer is already mapped.");

                MapBufferAccessMask access = mode switch
                {
                    GraphicsMapMode.Read => MapBufferAccessMask.MapReadBit,
                    GraphicsMapMode.Write =>
                        MapBufferAccessMask.MapWriteBit | MapBufferAccessMask.MapInvalidateRangeBit,
                    GraphicsMapMode.ReadWrite =>
                        MapBufferAccessMask.MapReadBit | MapBufferAccessMask.MapWriteBit,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };

                void* pointer = _device.DirectStateAccess.MapNamedBufferRange(
                    _handle, (nint)offset, (nuint)size, access);
                if (pointer == null)
                    throw new GraphicsException("OpenGL failed to map the requested buffer range.");

                OpenGLMappedMemoryManager manager = new(pointer, (int)size);
                _mapped = manager;
                _isMapped = true;
                return manager.Memory;
            }
        });
    }

    public void Unmap()
    {
        ThrowIfDisposed();
        bool contentsValid = _device.WithContext(gl =>
        {
            lock (_mappingGate)
            {
                ThrowIfDisposed();
                OpenGLMappedMemoryManager manager = _mapped ??
                    throw new InvalidOperationException("The buffer is not mapped.");

                bool valid = _device.DirectStateAccess.UnmapNamedBuffer(_handle);
                _isMapped = false;
                _mapped = null;
                manager.Invalidate();
                return valid;
            }
        });

        if (!contentsValid)
            throw new GraphicsException("OpenGL reported that the mapped buffer contents became corrupted during Unmap.");
    }

    protected override void DisposeCore()
    {
        try
        {
            // Device disposal already drains the registered native resource set. A late resource
            // disposal must not reactivate an already disposed context.
            if (!_device.IsDisposed)
                _device.ReleaseResource(this);
        }
        finally
        {
            lock (_mappingGate)
            {
                _mapped?.Invalidate();
                _mapped = null;
                // If destruction was deferred by an in-flight submission, ReleaseNative will
                // unmap the driver pointer after that submission completes.
            }
        }
    }

    public void ReleaseNative(GL gl)
    {
        lock (_mappingGate)
        {
            uint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle == 0)
                return;

            if (_isMapped)
            {
                _device.DirectStateAccess.UnmapNamedBuffer(handle);
                _isMapped = false;
            }

            _mapped?.Invalidate();
            _mapped = null;
            gl.DeleteBuffer(handle);
        }
    }
}
