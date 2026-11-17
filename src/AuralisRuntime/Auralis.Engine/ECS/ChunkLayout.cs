namespace Auralis.Engine.ECS;

/// <summary>
/// Computes a heuristic entity capacity for a chunk from its component signature.
/// Columns are separate typed arrays; the target is not a hard memory budget.
/// </summary>
/// <remarks>
/// SizeInBytes represents the inline storage footprint used by the component column.
/// For a reference-type component, Unsafe.SizeOf<T>() is the size of the reference stored
/// in the managed array, not the size of the object it points to. Object headers, fields,
/// referenced graphs, and other indirect managed allocations are intentionally not estimated.
/// Struct components that contain references are sized as structs, but their referenced
/// objects are likewise excluded. Treat TargetBytes as a row-capacity heuristic, not an
/// estimate of total managed heap usage.
/// </remarks>
public static class ChunkLayout
{
    public const int DefaultTargetBytes = 16 * 1024;
    public const int DefaultMaximumEntities = 512;

    public static int CalculateCapacity(
        ReadOnlySpan<int> componentTypeIds,
        int targetBytes = DefaultTargetBytes,
        int maximumEntities = DefaultMaximumEntities)
    {
        if (targetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        if (maximumEntities <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntities));

        // One compact EntityId per row; component column slots are accounted for below.
        // Managed object payloads are intentionally excluded; see the class remarks.
        long bytesPerEntity = sizeof(ulong);

        for (var i = 0; i < componentTypeIds.Length; i++)
        {
            var descriptor = ComponentTypeRegistry.GetById(componentTypeIds[i]);
            bytesPerEntity = checked(bytesPerEntity + Math.Max(1, descriptor.SizeInBytes));
        }

        var capacity = (int)Math.Clamp(targetBytes / Math.Max(1L, bytesPerEntity), 1, maximumEntities);
        return capacity;
    }
}
