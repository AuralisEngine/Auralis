using Auralis.Core.Mathematics;
using Auralis.Engine.ECS;

namespace Auralis.Engine.Transform;

/// <summary>Non-fatal hierarchy diagnostics reported by <see cref="TransformSystem"/>.</summary>
public enum TransformHierarchyError
{
    None,
    CycleDetected,
    ParentMissingTransform
}

/// <summary>
/// Resolves world transforms in three chunk-linear passes:
/// cache local/parent data, resolve parent chains using reusable arrays, then write results.
/// Invalid hierarchies are skipped and reported instead of aborting the entire frame.
/// </summary>
public sealed class TransformSystem : ISystem
{
    private static long s_updateVersion;

    private LocalTransform[] _localTransforms = Array.Empty<LocalTransform>();
    private EntityId[] _parents = Array.Empty<EntityId>();
    private Matrix[] _computedMatrices = Array.Empty<Matrix>();
    private ulong[] _transformVersions = Array.Empty<ulong>();
    private ulong[] _visitVersions = Array.Empty<ulong>();
    private byte[] _visitStates = Array.Empty<byte>();
    private EntityId[] _path = Array.Empty<EntityId>();

    /// <summary>Number of distinct invalid hierarchy chains encountered during the last update.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>The first entity associated with a hierarchy error in the last update.</summary>
    public EntityId LastErrorEntity { get; private set; }

    /// <summary>The first hierarchy error encountered during the last update.</summary>
    public TransformHierarchyError LastError { get; private set; }

    public void Execute(World world, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(world);

        var updateVersion = unchecked((ulong)Interlocked.Increment(ref s_updateVersion));
        if (updateVersion == 0)
            updateVersion = unchecked((ulong)Interlocked.Increment(ref s_updateVersion));

        ErrorCount = 0;
        LastErrorEntity = default;
        LastError = TransformHierarchyError.None;

        // Pass 1: read the dense columns sequentially. Parent is the only optional
        // component lookup; subsequent parent-chain traversal uses these cached arrays.
        foreach (var chunk in world.Query<LocalTransform, WorldTransform>())
        {
            var entities = chunk.Entities;
            var locals = chunk.First;

            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                var index = (int)entity.Index;
                EnsureScratchCapacity(index + 1);

                _localTransforms[index] = locals[i];
                _parents[index] = world.TryGetComponent<Parent>(entity, out var parent) && parent.HasParent
                    ? parent.Entity
                    : default;
                _transformVersions[index] = updateVersion;
            }
        }

        // Pass 2: calculate each transform at most once, following cached parent IDs.
        foreach (var chunk in world.Query<LocalTransform, WorldTransform>())
        {
            var entities = chunk.Entities;
            for (var i = 0; i < chunk.Count; i++)
                ResolveEntity(world, entities[i], updateVersion);
        }

        // Pass 3: commit only successfully resolved values, again in chunk order.
        foreach (var chunk in world.Query<LocalTransform, WorldTransform>())
        {
            var entities = chunk.Entities;
            var worlds = chunk.Second;

            for (var i = 0; i < chunk.Count; i++)
            {
                var index = (int)entities[i].Index;
                if (_transformVersions[index] != updateVersion ||
                    _visitVersions[index] != updateVersion ||
                    _visitStates[index] != 2)
                    continue;

                var worldTransform = worlds[i];
                worldTransform.Matrix = _computedMatrices[index];
                worldTransform.UpdateVersion = updateVersion;
                worlds[i] = worldTransform;
            }
        }
    }

    private void ResolveEntity(World world, EntityId entity, ulong updateVersion)
    {
        var pathCount = 0;
        var current = entity;

        while (world.IsAlive(current))
        {
            var index = (int)current.Index;

            // A live parent without both transform components is a content error.
            if ((uint)index >= (uint)_transformVersions.Length ||
                _transformVersions[index] != updateVersion)
            {
                MarkPathInvalid(pathCount);
                ReportError(current, TransformHierarchyError.ParentMissingTransform);
                return;
            }

            if (_visitVersions[index] == updateVersion)
            {
                if (_visitStates[index] == 1)
                {
                    MarkPathInvalid(pathCount);
                    ReportError(current, TransformHierarchyError.CycleDetected);
                    return;
                }

                if (_visitStates[index] == 3)
                {
                    MarkPathInvalid(pathCount);
                    return;
                }

                // State 2 means this ancestor was already resolved this update.
                break;
            }

            _visitVersions[index] = updateVersion;
            _visitStates[index] = 1;
            _path[pathCount++] = current;

            var parent = _parents[index];
            if (!parent.IsValid || !world.IsAlive(parent))
                break;

            current = parent;
        }

        // The path is stored child-to-parent. Resolve it in reverse so parents
        // are available before their children.
        while (pathCount > 0)
        {
            var currentEntity = _path[--pathCount];
            var index = (int)currentEntity.Index;
            var matrix = _localTransforms[index].ToMatrix();
            var parent = _parents[index];

            if (parent.IsValid && world.IsAlive(parent))
            {
                var parentIndex = (int)parent.Index;
                if (_visitVersions[parentIndex] != updateVersion ||
                    _visitStates[parentIndex] != 2)
                {
                    // This should only occur if the hierarchy was invalidated by
                    // malformed parent data. Skip the entire unresolved path.
                    _visitStates[index] = 3;
                    MarkPathInvalid(pathCount);
                    ReportError(parent, TransformHierarchyError.ParentMissingTransform);
                    return;
                }

                matrix *= _computedMatrices[parentIndex];
            }

            _computedMatrices[index] = matrix;
            _visitStates[index] = 2;
        }
    }

    private void MarkPathInvalid(int pathCount)
    {
        for (var i = 0; i < pathCount; i++)
        {
            var index = (int)_path[i].Index;
            _visitVersions[index] = _visitVersions[index]; // Keep the current visit stamp.
            _visitStates[index] = 3;
        }
    }

    private void ReportError(EntityId entity, TransformHierarchyError error)
    {
        ErrorCount++;
        if (LastError == TransformHierarchyError.None)
        {
            LastError = error;
            LastErrorEntity = entity;
        }
    }

    private void EnsureScratchCapacity(int requiredCapacity)
    {
        if (_path.Length >= requiredCapacity)
            return;

        var newCapacity = Math.Max(requiredCapacity, Math.Max(16, _path.Length * 2));
        Array.Resize(ref _path, newCapacity);
        Array.Resize(ref _localTransforms, newCapacity);
        Array.Resize(ref _parents, newCapacity);
        Array.Resize(ref _computedMatrices, newCapacity);
        Array.Resize(ref _transformVersions, newCapacity);
        Array.Resize(ref _visitVersions, newCapacity);
        Array.Resize(ref _visitStates, newCapacity);
    }
}
