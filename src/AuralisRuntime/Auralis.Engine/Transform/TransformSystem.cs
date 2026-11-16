using Auralis.Engine.ECS;

namespace Auralis.Engine.Transform;

/// <summary>
/// Updates cached world matrices in parent-before-child order without allocating a per-frame
/// hierarchy graph. Cycles are reported rather than silently producing stale transforms.
/// </summary>
public sealed class TransformSystem : ISystem
{
    private static long s_updateVersion;
    public void Execute(World world, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(world);
        var updateVersion = unchecked((ulong)Interlocked.Increment(ref s_updateVersion));
        if (updateVersion == 0)
            updateVersion = unchecked((ulong)Interlocked.Increment(ref s_updateVersion));

        var remaining = 0;
        foreach (var chunk in world.Query<LocalTransform, WorldTransform>())
            remaining += chunk.Count;

        for (var pass = 0; pass <= remaining && remaining > 0; pass++)
        {
            var progress = 0;
            foreach (var chunk in world.Query<LocalTransform, WorldTransform>())
            {
                var entities = chunk.Entities;
                var locals = chunk.First;
                var worlds = chunk.Second;

                for (var i = 0; i < chunk.Count; i++)
                {
                    if (worlds[i].UpdateVersion == updateVersion)
                        continue;

                    if (!world.TryGetComponent<Parent>(entities[i], out var parent) || !parent.HasParent ||
                        !world.IsAlive(parent.Entity))
                    {
                        worlds[i].Matrix = locals[i].ToMatrix();
                        worlds[i].UpdateVersion = updateVersion;
                        progress++;
                        continue;
                    }

                    if (!world.TryGetComponent<WorldTransform>(parent.Entity, out var parentWorld) ||
                        parentWorld.UpdateVersion != updateVersion)
                        continue;

                    worlds[i].Matrix = locals[i].ToMatrix() * parentWorld.Matrix;
                    worlds[i].UpdateVersion = updateVersion;
                    progress++;
                }
            }

            remaining -= progress;
            if (progress == 0)
                throw new InvalidOperationException("Transform hierarchy contains a cycle or a parent without LocalTransform and WorldTransform.");
        }
    }
}
