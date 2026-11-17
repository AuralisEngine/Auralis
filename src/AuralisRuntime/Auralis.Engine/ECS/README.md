# Auralis ECS

The ECS is an archetype-first runtime with an object-oriented Entity facade.

## Core decisions

- Entity is a managed, inheritable class. It is a convenient engine/game object facade.
- EntityId is a compact 64-bit, world-local runtime handle (32-bit slot + 32-bit generation).
- Persistent identity remains a GUID on ComponentBase; runtime handles are not serialization IDs.
- Component data is stored in archetypes and dense chunks, not as per-Entity object fields.
- Components default to GC-tracked typed arrays. This safely supports reference-containing component types.
- Unmanaged components may be explicitly registered for aligned native columns per World.
- Structural changes are immediate outside system execution and rejected during execution; workers record changes in an EntityCommandBuffer, which the World owner thread plays back afterward.
- Archetypes cache add/remove transition targets, avoiding repeated signature construction after a transition has been warmed.
- Archetype transitions create one owned signature array rather than an intermediate array plus a second signature copy.
- Chunks use the archetype's sorted component IDs to locate columns, avoiding a per-chunk component-index dictionary.
- Query-builder cache keys are structural value keys rather than allocated strings; each builder stores required/excluded IDs in one lazily allocated list and reuses its built query until With/Without changes the definition.
- Archetype column creation does not capture a per-archetype factory delegate; fixed-signature reserve APIs use stack storage instead of temporary arrays.
- Typed queries use cached archetype matches and value-type enumerators; after warm-up, typed query iteration does not allocate. ReadOnlyComponents/ReadOnlyFirst/ReadOnlySecond expose read-only spans.
- ReserveEntities and ReserveArchetype<T...> allow callers to preallocate chunk capacity before simulation.
- System access declarations are metadata for future scheduling. The current scheduler is deterministic and single-threaded.

## Example

~~~csharp
using Auralis.Engine;
using Auralis.Engine.ECS;

public struct Position { public float X, Y, Z; }
public struct Velocity { public float X, Y, Z; }

using var world = new World("Gameplay");
var player = world.CreateEntity<PlayerEntity>("Player");
player.AddComponent(new Position());
player.AddComponent(new Velocity());

foreach (var chunk in world.Query<Position, Velocity>())
{
    var positions = chunk.First;
    var velocities = chunk.Second;
    for (var i = 0; i < chunk.Count; i++)
    {
        positions[i].X += velocities[i].X;
        positions[i].Y += velocities[i].Y;
        positions[i].Z += velocities[i].Z;
    }
}

public sealed class PlayerEntity : Entity { }
~~~

## Storage and GC

Managed arrays are the safe default. The GC can see and update every reference in reference-containing components. Native columns are opt-in and only available for unmanaged component types. Native columns are allocated through Auralis.Core.MemoryUtilities and released with their owning chunks.

The current chunk layout uses a configurable target byte budget and maximum row count. Each component column is a separate typed array/allocation; this is columnar SoA across component types, not field-wise SoA within each struct. Query cache creation, archetype creation, and chunk growth can allocate; prewarm known signatures before entering hot simulation. Benchmark results should guide future layout changes. Component spans and ref component references must not be retained across structural changes or World disposal.

## Transform and scripting integration

- LocalTransform, WorldTransform, and Parent are value-type components using Auralis.Core.Mathematics.
- TransformSystem resolves parent chains iteratively. It uses reusable scratch arrays, processes every participating entity at most once per update, and reports cycles or missing parent transform data.
- Script remains a managed ComponentBase-derived behavior. Store it using the base component type (entity.AddComponent<Script>(script)) for ScriptSystem to discover and update it.
- Transform data lives in ECS components only; there is no separate Entity.Transform object or legacy TransformComponent wrapper.

## Current limitations

- World structural mutation is owner-thread-only.
- The scheduler does not yet run jobs in parallel or perform work stealing. Access declarations currently describe conflicts but are not yet used to build parallel batches.
- The World is not thread-safe. Structural changes and command-buffer playback must run on the World owner thread. `EntityCommandBuffer` supports concurrent recording from multiple workers through an internal lock; recording is serialized, and concurrent command order follows lock acquisition, so it is not deterministic across runs. Use one buffer per worker and play them back in a fixed order when deterministic ordering matters.
- Query component spans are writable by default; read-only span views are available, but the scheduler does not yet enforce access modes.
- EntityCommandBuffer playback is single-use: if a command throws, already-applied changes remain, but the failing command and all remaining commands are discarded and the buffer is consumed. Call Clear before reusing it; playback is intentionally not transactional, so failed batches must be reconstructed explicitly if they are to be retried.
- Command buffers currently allocate one command object per recorded operation.
- Shared components, dynamic buffers, singleton components, prefabs, serialization, snapshots, replication, and source generation are future extensions.
- The first version prioritizes correctness and a stable extension surface over claiming unmeasured performance.
