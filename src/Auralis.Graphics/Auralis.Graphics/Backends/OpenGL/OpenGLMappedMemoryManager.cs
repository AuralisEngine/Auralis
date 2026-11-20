using System.Buffers;
using System.Runtime.InteropServices;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Memory returned by IGraphicsBuffer.Map. Disposing the manager invalidates future Memory/Span
/// access instead of leaving a usable managed array behind after Unmap.
/// </summary>
internal sealed unsafe class OpenGLMappedMemoryManager : MemoryManager<byte>
{
    private byte[]? _buffer;

    internal OpenGLMappedMemoryManager(byte[] buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public override Span<byte> GetSpan()
    {
        byte[]? buffer = Volatile.Read(ref _buffer);
        return buffer is null
            ? throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager))
            : buffer.AsSpan();
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        byte[] buffer = GetBuffer();
        if ((uint)elementIndex > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, handle);
    }

    public override void Unpin()
    {
        // MemoryHandle owns and releases the GCHandle returned by Pin.
    }

    internal void Invalidate() => Dispose(disposing: true);

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _buffer, null);
    }

    internal byte[] GetBuffer() =>
        Volatile.Read(ref _buffer) ??
        throw new ObjectDisposedException(nameof(OpenGLMappedMemoryManager));
}
