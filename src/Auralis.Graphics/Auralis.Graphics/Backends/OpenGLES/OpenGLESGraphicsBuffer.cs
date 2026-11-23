using System.Buffers;
using System.Runtime.InteropServices;
using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGLES;

namespace Auralis.Graphics.Backends.OpenGLES;

/// <summary>
/// OpenGL ES buffer using the ES 3.x bind-to-edit API. CPU mappings use transient map/unmap operations;
/// persistent mapping and desktop buffer-storage flags are never assumed.
/// </summary>
internal sealed unsafe class OpenGLESGraphicsBuffer : OpenGLESNativeDeviceObject, IGraphicsBuffer
{
    private readonly OpenGLESGraphicsDevice _device;
    private readonly Lock _mapGate = new();
    private uint _handle;
    private OpenGLESMappedStagingMemoryManager? _mapped;
    private ulong _mappedOffset;
    private GraphicsMapMode _mappedMode;

    private OpenGLESGraphicsBuffer(
        OpenGLESGraphicsDevice device,
        in GraphicsBufferDescription description,
        uint handle) : base(device)
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
            uint value = Volatile.Read(ref _handle);
            return value != 0 ? value : throw new ObjectDisposedException(nameof(OpenGLESGraphicsBuffer));
        }
    }

    internal static OpenGLESGraphicsBuffer CreateUnderContext(
        OpenGLESGraphicsDevice device,
        GL gl,
        in GraphicsBufferDescription description)
    {
        if (description.SizeInBytes > (ulong)nuint.MaxValue)
            throw new GraphicsValidationException("The requested buffer exceeds the native addressable size.");

        uint handle = gl.GenBuffer();
        if (handle == 0)
            throw new GraphicsException("OpenGL ES failed to allocate a buffer name.");

        try
        {
            gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, handle);
            BufferUsageARB usage = description.MemoryAccess switch
            {
                GraphicsMemoryAccess.GpuOnly => BufferUsageARB.StaticDraw,
                GraphicsMemoryAccess.CpuToGpu => BufferUsageARB.DynamicDraw,
                GraphicsMemoryAccess.GpuToCpu => BufferUsageARB.StreamRead,
                GraphicsMemoryAccess.CpuReadWrite => BufferUsageARB.DynamicDraw,
                _ => throw new ArgumentOutOfRangeException(nameof(description))
            };
            gl.BufferData(BufferTargetARB.CopyWriteBuffer, (nuint)description.SizeInBytes, null, usage);
            gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);

            OpenGLESGraphicsBuffer buffer = new(device, description, handle);
            device.RegisterUnderContext(buffer);
            return buffer;
        }
        catch
        {
            gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
            gl.DeleteBuffer(handle);
            throw;
        }
    }

    /// <summary>
    /// Maps a range through managed staging memory. Read and ReadWrite modes snapshot the GPU buffer
    /// during Map; Write and ReadWrite upload the entire mapped range through BufferSubData during Unmap.
    /// OpenGL ES persistent/native buffer mapping is not used.
    /// </summary>
    /// <remarks>
    /// Before consuming GPU-produced results, callers must order and complete the producing work,
    /// such as by waiting for its submission fence. A read mapping is a snapshot, not a live view.
    /// Write-only mapping performs no GL calls and does not acquire the device context.
    /// </remarks>
    /// <param name="mode">Read, write, or read/write access permitted by the buffer's creation-time memory-access flags.</param>
    /// <param name="offset">Byte offset from the start of the buffer.</param>
    /// <param name="size">Number of bytes to map, or <see cref="ulong.MaxValue" /> for the remaining range.</param>
    /// <returns>Managed staging memory valid until <see cref="Unmap" /> or disposal; outstanding pins defer returning its array to the pool.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range or mode is invalid.</exception>
    /// <exception cref="GraphicsValidationException">The creation-time CPU access does not permit the requested mapping direction.</exception>
    /// <exception cref="InvalidOperationException">The buffer is already mapped.</exception>
    /// <exception cref="GraphicsException">A read staging snapshot cannot be copied from the native buffer.</exception>
    public Memory<byte> Map(GraphicsMapMode mode, ulong offset = 0, ulong size = ulong.MaxValue)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        bool cpuReadable = Description.MemoryAccess is GraphicsMemoryAccess.GpuToCpu or GraphicsMemoryAccess.CpuReadWrite;
        bool cpuWritable = Description.MemoryAccess is GraphicsMemoryAccess.CpuToGpu or GraphicsMemoryAccess.CpuReadWrite;
        if (mode is GraphicsMapMode.Read or GraphicsMapMode.ReadWrite && !cpuReadable)
            throw new GraphicsValidationException("The buffer was not created with CPU-readable memory access.");
        if (mode is GraphicsMapMode.Write or GraphicsMapMode.ReadWrite && !cpuWritable)
            throw new GraphicsValidationException("The buffer was not created with CPU-writable memory access.");

        if (size == ulong.MaxValue)
        {
            if (offset > Description.SizeInBytes)
                throw new ArgumentOutOfRangeException(nameof(offset));
            size = Description.SizeInBytes - offset;
        }

        if (size == 0 || offset > Description.SizeInBytes ||
            size > Description.SizeInBytes - offset || size > int.MaxValue ||
            offset > (ulong)nint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(size), "The mapped range is empty or outside the buffer.");

        if (mode == GraphicsMapMode.Write)
        {
            lock (_mapGate)
            {
                ThrowIfDisposed();
                if (_mapped is not null)
                    throw new InvalidOperationException("The buffer is already mapped.");

                OpenGLESMappedStagingMemoryManager manager = new((int)size);
                _mapped = manager;
                _mappedOffset = offset;
                _mappedMode = mode;
                return manager.Memory;
            }
        }

        using OpenGLESContextScope context = _device.AcquireContext();
        GL gl = context.Gl;
        lock (_mapGate)
        {
            ThrowIfDisposed();
            if (_mapped is not null)
                throw new InvalidOperationException("The buffer is already mapped.");

            OpenGLESMappedStagingMemoryManager manager = new((int)size);
            try
            {
                if (mode is GraphicsMapMode.Read or GraphicsMapMode.ReadWrite)
                {
                    gl.BindBuffer(BufferTargetARB.CopyReadBuffer, Handle);
                    void* pointer = null;
                    bool unmapAttempted = false;
                    try
                    {
                        pointer = gl.MapBufferRange(
                            BufferTargetARB.CopyReadBuffer,
                            (nint)offset,
                            (nuint)size,
                            MapBufferAccessMask.ReadBit);
                        if (pointer == null)
                            throw new GraphicsException("OpenGL ES failed to map the requested buffer range for reading.");

                        new ReadOnlySpan<byte>(pointer, (int)size).CopyTo(manager.GetSpan());
                        unmapAttempted = true;
                        if (!gl.UnmapBuffer(BufferTargetARB.CopyReadBuffer))
                            throw new GraphicsException("OpenGL ES reported corrupted buffer contents while unmapping a read range.");
                    }
                    finally
                    {
                        if (pointer != null && !unmapAttempted)
                        {
                            try { gl.UnmapBuffer(BufferTargetARB.CopyReadBuffer); }
                            finally { gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0); }
                        }
                        else
                        {
                            gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
                        }
                    }
                }

                _mapped = manager;
                _mappedOffset = offset;
                _mappedMode = mode;
                return manager.Memory;
            }
            catch
            {
                manager.Invalidate();
                throw;
            }
        }
    }

    /// <summary>
    /// Ends the staging map and invalidates previously returned memory views. Write and ReadWrite
    /// mappings upload the complete mapped range, including bytes the caller did not modify.
    /// </summary>
    /// <remarks>Write and ReadWrite modes upload the complete mapped range, not only modified bytes. Read mode performs no upload.</remarks>
    /// <exception cref="InvalidOperationException">The buffer is not mapped.</exception>
    /// <exception cref="GraphicsException">GLES reports a mapping or buffer update failure.</exception>
    public void Unmap()
    {
        ThrowIfDisposed();
        using OpenGLESContextScope context = _device.AcquireContext();
        GL gl = context.Gl;
        lock (_mapGate)
        {
            ThrowIfDisposed();
            OpenGLESMappedStagingMemoryManager manager = _mapped ??
                throw new InvalidOperationException("The buffer is not mapped.");

            if (_mappedMode is GraphicsMapMode.Write or GraphicsMapMode.ReadWrite)
            {
                ReadOnlySpan<byte> data = manager.GetSpan();
                try
                {
                    gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, Handle);
                    fixed (byte* pointer = data)
                        gl.BufferSubData(
                            BufferTargetARB.CopyWriteBuffer,
                            (nint)_mappedOffset,
                            (nuint)data.Length,
                            pointer);
                }
                finally
                {
                    gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
                }
            }

            _mapped = null;
            _mappedOffset = 0;
            manager.Invalidate();
        }
    }

    protected override void DisposeCore()
    {
        OpenGLESMappedStagingMemoryManager? mapped;
        lock (_mapGate)
        {
            mapped = _mapped;
            _mapped = null;
        }
        mapped?.Invalidate();
        ReleaseOwnerReference();
    }

    public override void ReleaseNative(GL gl)
    {
        lock (_mapGate)
        {
            uint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle == 0)
                return;
            _mapped?.Invalidate();
            _mapped = null;
            gl.DeleteBuffer(handle);
        }
    }
}

/// <summary>Owns the pooled managed byte array exposed by an OpenGL ES buffer staging map.</summary>
/// <remarks>
/// The manager can be invalidated while callers still hold <see cref="MemoryHandle" /> pins. New
/// span and pin requests then fail immediately, while the pinned array is returned to the pool only
/// after the final outstanding handle is unpinned.
/// </remarks>
internal sealed unsafe class OpenGLESMappedStagingMemoryManager : MemoryManager<byte>
{
    private readonly Lock _gate = new();
    private readonly int _length;
    private byte[]? _buffer;
    private GCHandle _pinHandle;
    private int _pinCount;
    private bool _disposeRequested;

    internal OpenGLESMappedStagingMemoryManager(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _length = length;
        _buffer = ArrayPool<byte>.Shared.Rent(length);
    }

    /// <summary>Returns the mapped staging range while the manager remains valid.</summary>
    /// <exception cref="ObjectDisposedException">The mapping has been invalidated.</exception>
    public override Span<byte> GetSpan()
    {
        lock (_gate)
        {
            if (_disposeRequested)
                throw new ObjectDisposedException(nameof(OpenGLESMappedStagingMemoryManager));
            byte[] buffer = _buffer ??
                throw new ObjectDisposedException(nameof(OpenGLESMappedStagingMemoryManager));
            return buffer.AsSpan(0, _length);
        }
    }

    /// <summary>Pins the backing array and returns a handle that must be released through <see cref="Unpin" />.</summary>
    /// <param name="elementIndex">Zero-based byte index within the mapped range.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the mapped range.</exception>
    /// <exception cref="ObjectDisposedException">The mapping has been invalidated.</exception>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        lock (_gate)
        {
            if (_disposeRequested)
                throw new ObjectDisposedException(nameof(OpenGLESMappedStagingMemoryManager));
            byte[] buffer = _buffer ??
                throw new ObjectDisposedException(nameof(OpenGLESMappedStagingMemoryManager));
            if ((uint)elementIndex > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            if (_pinCount == int.MaxValue)
                throw new InvalidOperationException("The staging memory has too many outstanding pins.");

            if (!_pinHandle.IsAllocated)
                _pinHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            _pinCount++;
            byte* pointer = (byte*)_pinHandle.AddrOfPinnedObject() + elementIndex;
            // The manager owns the single GCHandle so it can free the pin before returning the
            // array to the pool. MemoryHandle still calls Unpin through this manager.
            return new MemoryHandle(pointer, default, this);
        }
    }

    /// <summary>Releases one pin and returns the pooled array when this was the final pin after disposal.</summary>
    public override void Unpin()
    {
        byte[]? bufferToReturn = null;
        lock (_gate)
        {
            if (_pinCount == 0)
                return;

            _pinCount--;
            if (_pinCount == 0)
            {
                FreePinHandleUnderLock();
                if (_disposeRequested)
                {
                    bufferToReturn = _buffer;
                    _buffer = null;
                }
            }
        }

        if (bufferToReturn is not null)
            ArrayPool<byte>.Shared.Return(bufferToReturn);
    }

    internal void Invalidate() => Dispose(true);

    protected override void Dispose(bool disposing)
    {
        byte[]? bufferToReturn = null;
        lock (_gate)
        {
            _disposeRequested = true;
            if (_pinCount == 0)
            {
                FreePinHandleUnderLock();
                bufferToReturn = _buffer;
                _buffer = null;
            }
        }

        if (bufferToReturn is not null)
            ArrayPool<byte>.Shared.Return(bufferToReturn);
    }

    private void FreePinHandleUnderLock()
    {
        if (!_pinHandle.IsAllocated)
            return;

        GCHandle handle = _pinHandle;
        _pinHandle = default;
        handle.Free();
    }
}
