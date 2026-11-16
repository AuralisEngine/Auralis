using Auralis.Engine.ECS;

namespace Auralis.Engine.Transform;

/// <summary>
/// Updates cached world matrices by walking each entity's parent chain iteratively.
/// Each participating entity is resolved at most once per update, so deep hierarchies
/// do not require repeated whole-world passes or recursive calls.
/// </summary>
public sealed class TransformSystem : ISystem
{
    private static long s_updateVersion;

    private ulong[] _visitVersions = Array.Empty<ulong>();
    private byte[] _visitStates = Array.Empty<byte>();
    private EntityId[] _path = Array.Empty<EntityId>();

    public void Execute(World world, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(world);

        var updateVersion = unchecked((ulong)Interlocked.Increment(ref s_updateVersion));
        if (updateVersion == 0)
            updateVersion = unchecked((ulong)Interlocked.Increment(ref s_updateVersion));

        foreach (var chunk in world.Query<LocalTransform, WorldTransform>())
        {
            var entities = chunk.Entities;
            for (var i = 0; i < entities.Length; i++)
                UpdateEntity(world, entities[i], updateVersion);
        }
    }

    private void UpdateEntity(World world, EntityId entity, ulong updateVersion)
    {
        var pathCount = 0;
        var current = entity;

        // Walk toward the root. State 1 means the entity is on this path; state 2
        // means its world transform has already been resolved for this update.
        while (world.IsAlive(current))
        {
            var index = (int)current.Index;
            EnsureScratchCapacity(index + 1);

            if (_visitVersions[index] == updateVersion)
            {
                if (_visitStates[index] == 1)
                    throw new InvalidOperationException("Transform hierarchy contains a cycle.");

                break;
            }

            if (!world.TryGetComponent<LocalTransform>(current, out _) ||
                !world.TryGetComponent<WorldTransform>(current, out _))
            {
                throw new InvalidOperationException(
                    "Every entity in a transform hierarchy must have both LocalTransform and WorldTransform.");
            }

            _visitVersions[index] = updateVersion;
            _visitStates[index] = 1;
            _path[pathCount++] = current;

            if (!world.TryGetComponent<Parent>(current, out var parent) ||
                !parent.HasParent ||
                !world.IsAlive(parent.Entity))
            {
                break;
            }

            current = parent.Entity;
        }

        // Resolve in reverse order so every valid parent is ready before its child.
        while (pathCount > 0)
        {
            var currentEntity = _path[--pathCount];
            var local = world.GetComponent<LocalTransform>(currentEntity);
            var matrix = local.ToMatrix();

            if (world.TryGetComponent<Parent>(currentEntity, out var parent) &&
                parent.HasParent &&
                world.IsAlive(parent.Entity))
            {
                if (!world.TryGetComponent<WorldTransform>(parent.Entity, out var parentTransform) ||
                    parentTransform.UpdateVersion != updateVersion)
                {
                    throw new InvalidOperationException(
                        "A parent transform could not be resolved. Check the hierarchy for missing transform components or cycles.");
                }

                matrix *= parentTransform.Matrix;
            }

            ref var worldTransform = ref world.GetComponentReference<WorldTransform>(currentEntity);
            worldTransform.Matrix = matrix;
            worldTransform.UpdateVersion = updateVersion;
            _visitStates[(int)currentEntity.Index] = 2;
        }
    }

    private void EnsureScratchCapacity(int requiredCapacity)
    {
        if (_path.Length >= requiredCapacity)
            return;

        var newCapacity = Math.Max(requiredCapacity, Math.Max(16, _path.Length * 2));
        Array.Resize(ref _path, newCapacity);
        Array.Resize(ref _visitVersions, newCapacity);
        Array.Resize(ref _visitStates, newCapacity);
    }
}
