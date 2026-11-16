# Auralis ECS

The ECS is an archetype-first runtime with an object-oriented Entity facade.

## Core decisions

- `Entity` is a managed, inheritable class. It is a convenient engine/game object facade.
- `EntityId` is a compact 64-bit, world-local runtime handle (32-bit slot + 32-bit generation).
- Persistent identity remains a GUID on `ComponentBase`; runtime handles are not serialization IDs.
- Component data is stored in archetypes and dense chunks, not as per-Entity object fields.
- Components default to GC-tracked typed arrays. This safely supports reference-containing component types.
- Unmanaged components may be explicitly registered for aligned native columns per World.
- Structural changes are immediate outside system execution and rejected during execution; record them in an `EntityCommandBuffer` and play it back afterward.
- Archetypes cache add/remove transition targets, avoiding repeated signature construction after a transition has been warmed.
- Typed queries use cached archetype matches and value-type enumerators; after warm-up, typed query iteration does not allocate. `ReadOnlyComponents`/`ReadOnlyFirst`/`ReadOnlySecond` expose read-only spans.
- `ReserveEntities` and `ReserveArchetype<T...>` allow callers to preallocate chunk capacity before simulation.
- System access declarations are metadata for future scheduling. The current scheduler is deterministic and single-threaded.

## Example

```csharp
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
```

## Storage and GC

Managed arrays are the safe default. The GC can see and update every reference in reference-containing components. Native columns are opt-in and only available for unmanaged component types. Native columns are allocated through `Auralis.Core.MemoryUtilities` and released with their owning chunks.

The current chunk layout uses a configurable target byte budget and maximum row count. Each component column is a separate typed array/allocation; this is columnar SoA across component types, not field-wise SoA within each struct. Query cache creation, archetype creation, and chunk growth can allocate; prewarm known signatures before entering hot simulation. Benchmark results should guide future layout changes. Component spans and `ref` component references must not be retained across structural changes or World disposal.

## Transform and scripting integration

- `LocalTransform`, `WorldTransform`, and `Parent` are value-type components using `Auralis.Core.Mathematics`.
- `TransformSystem` updates parent-before-child world matrices using version stamps and reports cycles or missing parent transform data. Add both `LocalTransform` and `WorldTransform` to every entity that should participate.
- The old `Entity.Transform` / `TransformComponent` API remains as a compatibility facade; it is not the storage model used by `TransformSystem`.
- `Script` remains a managed `ComponentBase`-derived behavior. Store it using the base component type (`entity.AddComponent<Script>(script)`) for `ScriptSystem` to discover and update it.

## Current limitations

- World structural mutation is owner-thread-only.
- The scheduler does not yet run jobs in parallel or perform work stealing. Access declarations currently describe conflicts but are not yet used to build parallel batches.
- The World is not thread-safe. Structural changes are owner-thread-only; workers should record commands into separate command buffers and let the owner thread play them back.
- Query component spans are writable by default; read-only span views are available, but the scheduler does not yet enforce access modes.
- Command buffers currently allocate one command object per recorded operation.
- Shared components, dynamic buffers, singleton components, prefabs, serialization, snapshots, replication, and source generation are future extensions.
- The first version prioritizes correctness and a stable extension surface over claiming unmeasured performance.
