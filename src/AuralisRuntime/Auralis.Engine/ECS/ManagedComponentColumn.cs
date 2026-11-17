namespace Auralis.Engine.ECS;

/// <summary>
/// GC-tracked storage for managed components and for components that contain references.
/// </summary>
internal sealed class ManagedComponentColumn<T> : IComponentColumn<T> where T : notnull
{
    private readonly T[] _items;

    public int ComponentTypeId => ComponentType<T>.Id;
    public int Capacity => _items.Length;
    public Span<T> Span => _items;

    internal ManagedComponentColumn(int capacity) => _items = new T[capacity];

    public ref T GetReference(int index) => ref _items[index];
    public ref readonly T GetReadOnlyReference(int index) => ref _items[index];

    public void Clear(int index) => _items[index] = default!;

    public void CopyElementTo(int sourceIndex, IComponentColumn destination, int destinationIndex)
    {
        if (destination is not IComponentColumn<T> typedDestination)
            throw new InvalidOperationException("Component column types do not match.");

        typedDestination.Span[destinationIndex] = _items[sourceIndex];
    }

    public void Dispose()
    {
        // The array is managed. Clearing releases references promptly when the chunk is disposed.
        Array.Clear(_items);
    }
}
