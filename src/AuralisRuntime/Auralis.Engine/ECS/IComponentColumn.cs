namespace Auralis.Engine.ECS;

internal interface IComponentColumn : IDisposable
{
    int ComponentTypeId { get; }
    int Capacity { get; }
    void Clear(int index);
    void CopyElementTo(int sourceIndex, IComponentColumn destination, int destinationIndex);
}

internal interface IComponentColumn<T> : IComponentColumn where T : notnull
{
    Span<T> Span { get; }
    ref T GetReference(int index);
    ref readonly T GetReadOnlyReference(int index);
}
