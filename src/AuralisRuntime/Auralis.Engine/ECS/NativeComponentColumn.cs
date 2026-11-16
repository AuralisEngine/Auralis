using Auralis.Core;
using System.Runtime.CompilerServices;

namespace Auralis.Engine.ECS;

/// <summary>Aligned native storage for explicitly registered unmanaged component types.</summary>
internal sealed unsafe class NativeComponentColumn<T> : IComponentColumn<T> where T : unmanaged
{
    private nint _memory;
    private readonly int _capacity;

    public int ComponentTypeId => ComponentType<T>.Id;
    public int Capacity => _capacity;
    public Span<T> Span => _memory == 0
        ? throw new ObjectDisposedException(nameof(NativeComponentColumn<T>))
        : new Span<T>((void*)_memory, _capacity);

    internal NativeComponentColumn(int capacity, int alignment)
    {
        _capacity = capacity;
        var byteCount = checked(capacity * Unsafe.SizeOf<T>());
        var allocationBytes = checked((byteCount + alignment - 1) & ~(alignment - 1));
        _memory = MemoryUtilities.AllocateAligned(allocationBytes, alignment);
        if (_memory == 0)
            throw new OutOfMemoryException("Unable to allocate native component storage.");
        MemoryUtilities.Clear(_memory, (uint)allocationBytes);
    }

    public ref T GetReference(int index) => ref Span[index];
    public void Clear(int index) => Span[index] = default;

    public void CopyElementTo(int sourceIndex, IComponentColumn destination, int destinationIndex)
    {
        if (destination is not IComponentColumn<T> typedDestination)
            throw new InvalidOperationException("Component column types do not match.");
        typedDestination.Span[destinationIndex] = Span[sourceIndex];
    }

    public void Dispose()
    {
        if (_memory == 0) return;
        MemoryUtilities.FreeAligned(_memory);
        _memory = 0;
    }
}
