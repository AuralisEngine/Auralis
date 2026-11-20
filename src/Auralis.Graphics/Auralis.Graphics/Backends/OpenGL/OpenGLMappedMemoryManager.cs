using System.Buffers;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Memory over an OpenGL mapped range. The view is invalidated immediately after Unmap or resource
/// disposal; it never exposes a staging array or pins managed storage.
/// </summary>
internal sealed unsafe class OpenGLMappedMemoryManager : MemoryManager<byte>
{
    private nint _pointer;
    private readonly int _length;

    internal OpenGLMappedMemoryManager(void* pointer, int length)
    {
        if (pointer == null)
            throw new ArgumentNullException(nameof(pointer));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        _pointer = (nint)pointer;
        _length = length;
    }

    public override Span<byte> GetSpan()
    {
        nint pointer = Interlocked.CompareExchange(ref _pointer, 0, 0);
        return pointer == 0
            ? throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager))
            : new Span<byte>((void*)pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        nint pointer = Interlocked.CompareExchange(ref _pointer, 0, 0);
        if (pointer == 0)
            throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager));

        return new MemoryHandle((byte*)pointer + elementIndex);
    }

    public override void Unpin()
    {
        // The pointer refers to driver-managed memory and is not moved by the GC.
    }

    internal void Invalidate() => Dispose(disposing: true);

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _pointer, 0);
    }
}
