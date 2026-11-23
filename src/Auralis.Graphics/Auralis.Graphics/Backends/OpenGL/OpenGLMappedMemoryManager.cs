using System.Buffers;
using System.Runtime.InteropServices;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Memory over an OpenGL mapped range. The view is invalidated immediately after Unmap or resource
/// disposal. Active MemoryHandle pins are tracked so the native mapping is not released underneath them.
/// </summary>
internal sealed unsafe class OpenGLMappedMemoryManager : MemoryManager<byte>
{
    private readonly object _gate = new();
    private nint _pointer;
    private readonly int _length;
    private int _pinCount;

    internal OpenGLMappedMemoryManager(void* pointer, int length)
        : this((nint)pointer, length)
    {
    }

    internal OpenGLMappedMemoryManager(nint pointer, int length)
    {
        if (pointer == 0)
            throw new ArgumentNullException(nameof(pointer));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        _pointer = pointer;
        _length = length;
    }

    /// <summary>Returns the mapped range while it is valid.</summary>
    /// <exception cref="ObjectDisposedException">The native mapping has been invalidated.</exception>
    public override Span<byte> GetSpan()
    {
        lock (_gate)
        {
            if (_pointer == 0)
                throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager));

            return new Span<byte>((void*)_pointer, _length);
        }
    }

    /// <summary>Pins the mapped native pointer until the returned handle is unpinned.</summary>
    /// <param name="elementIndex">Zero-based byte offset within the mapped range.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index exceeds the mapped range.</exception>
    /// <exception cref="ObjectDisposedException">The mapping has been invalidated.</exception>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        lock (_gate)
        {
            if (_pointer == 0)
                throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager));

            _pinCount++;
            return new MemoryHandle((byte*)_pointer + elementIndex, default, this);
        }
    }

    /// <summary>Releases a pin previously created by <see cref="Pin" />.</summary>
    /// <exception cref="InvalidOperationException">No outstanding pin exists.</exception>
    public override void Unpin()
    {
        lock (_gate)
        {
            if (_pinCount <= 0)
                throw new InvalidOperationException("Mapped-memory pin accounting became unbalanced.");

            _pinCount--;
            if (_pinCount == 0)
                Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Ensures the native mapping can be unmapped without invalidating an active pin.</summary>
    /// <exception cref="InvalidOperationException">One or more memory handles are still pinned.</exception>
    /// <exception cref="ObjectDisposedException">The mapping is already invalid.</exception>
    internal void ValidateCanUnmap()
    {
        lock (_gate)
        {
            if (_pinCount != 0)
                throw new InvalidOperationException(
                    "The mapped buffer cannot be unmapped while one or more MemoryHandle pins are active.");
            if (_pointer == 0)
                throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager));
        }
    }

    /// <summary>Invalidates future Memory/Span/Pin operations but leaves existing native pins valid.</summary>
    internal void Invalidate() => Dispose(disposing: true);

    /// <summary>Checks that no caller still holds a pointer into the mapping before native release.</summary>
    /// <exception cref="InvalidOperationException">One or more memory handles remain pinned.</exception>
    internal void ValidateNoPins()
    {
        lock (_gate)
        {
            if (_pinCount != 0)
                throw new InvalidOperationException(
                    "The mapped buffer cannot be released while one or more MemoryHandle pins are active.");
        }
    }

    /// <summary>Waits until previously issued MemoryHandle pins have been released.</summary>
    internal void WaitForPins()
    {
        lock (_gate)
        {
            while (_pinCount != 0)
                Monitor.Wait(_gate);
        }
    }

    /// <summary>Invalidates the mapped pointer so subsequent span and pin requests fail.</summary>
    /// <remarks>Existing pins are not forcibly revoked; the buffer/device lifetime protocol verifies or waits for them before unmapping the native storage.</remarks>
    protected override void Dispose(bool disposing)
    {
        lock (_gate)
            _pointer = 0;
    }
}
