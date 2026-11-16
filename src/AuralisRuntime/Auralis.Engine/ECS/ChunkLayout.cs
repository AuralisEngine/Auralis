namespace Auralis.Engine.ECS;

/// <summary>
/// Computes a practical entity capacity for a chunk from its component signature.
/// Columns are separate typed arrays; this layout is a capacity budget, not one packed allocation.
/// </summary>
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

        // One compact EntityId per row; component columns are accounted for below.
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
