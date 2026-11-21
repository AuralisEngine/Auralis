using Auralis.Graphics.Abstractions;
using Auralis.Graphics.Devices;
using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

internal interface IOpenGLNativeResource
{
    bool TryRetain();
    void ReleaseReference();
    void ReleaseNative(GL gl);
}

/// <summary>
/// Device object whose native handle is retained independently from public Dispose state.
/// The high bit closes the owner reference; the remaining bits count the owner and recorded uses.
/// </summary>
internal abstract class OpenGLNativeDeviceObject : GraphicsDeviceObject, IOpenGLNativeResource
{
    private const int ClosingMask = int.MinValue;
    private const int ReferenceMask = int.MaxValue;
    private readonly OpenGLGraphicsDevice _device;
    private int _referenceState = 1;

    protected OpenGLNativeDeviceObject(OpenGLGraphicsDevice device) : base(device) =>
        _device = device;

    public bool TryRetain()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            int references = state & ReferenceMask;
            if ((state & ClosingMask) != 0 || references == 0)
                return false;
            if (references == ReferenceMask)
                throw new InvalidOperationException("The OpenGL native resource reference count overflowed.");

            if (Interlocked.CompareExchange(ref _referenceState, state + 1, state) == state)
                return true;
        }
    }

    public void ReleaseReference()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            int references = state & ReferenceMask;
            if (references == 0)
                throw new InvalidOperationException("An OpenGL native resource reference was released more than once.");

            int nextState = (state & ClosingMask) | (references - 1);
            if (Interlocked.CompareExchange(ref _referenceState, nextState, state) != state)
                continue;

            if (references == 1)
                _device.ReleaseResource(this);
            return;
        }
    }

    protected void ReleaseOwnerReference()
    {
        while (true)
        {
            int state = Volatile.Read(ref _referenceState);
            if ((state & ClosingMask) != 0)
                throw new InvalidOperationException("The OpenGL native resource owner reference was already released.");

            if (Interlocked.CompareExchange(
                    ref _referenceState, state | ClosingMask, state) == state)
                break;
        }

        ReleaseReference();
    }

    public abstract void ReleaseNative(GL gl);
}

/// <summary>Native OpenGL buffer with directly mapped driver memory.</summary>
internal sealed unsafe class OpenGLGraphicsBuffer : OpenGLNativeDeviceObject, IGraphicsBuffer
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
            uint handle = Volatile.Read(ref _handle);
            return handle != 0 ? handle : throw new ObjectDisposedException(nameof(OpenGLGraphicsBuffer));
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
                    GraphicsMapMode.Read => MapBufferAccessMask.ReadBit,
                    GraphicsMapMode.Write =>
                        MapBufferAccessMask.WriteBit | MapBufferAccessMask.InvalidateRangeBit,
                    GraphicsMapMode.ReadWrite =>
                        MapBufferAccessMask.ReadBit | MapBufferAccessMask.WriteBit,
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

                manager.ValidateCanUnmap();
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
        // Invalidate new Memory/Span/Pin access immediately, but retain the manager object until
        // ReleaseNative runs. Deferred destruction must still observe existing MemoryHandle pins
        // before unmapping the driver pointer.
        lock (_mappingGate)
            _mapped?.Invalidate();

        ReleaseOwnerReference();
    }

    public override void ReleaseNative(GL gl)
    {
        lock (_mappingGate)
        {
            uint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle == 0)
                return;

            if (_isMapped)
            {
                _mapped?.WaitForPins();
                _device.DirectStateAccess.UnmapNamedBuffer(handle);
                _isMapped = false;
            }

            _mapped?.Invalidate();
            _mapped = null;
            gl.DeleteBuffer(handle);
        }
    }
}
