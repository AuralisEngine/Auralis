# Auralis ECS

The ECS is an archetype-first runtime with an object-oriented Entity facade.

## Core decisions

- Entity is a managed, inheritable class. It is a convenient engine/game object facade.
- EntityId is a compact 64-bit, world-local runtime handle (32-bit slot + 32-bit generation). A foreign-world ID can numerically alias a live ID in another World; always keep the owning World alongside an EntityId.
- Persistent identity remains a read-only GUID on ComponentBase; runtime handles are not serialization IDs.
- Component data is stored in archetypes and dense chunks, not as per-Entity object fields.
- Components default to GC-tracked typed arrays. This safely supports reference-containing component types.
- Unmanaged components may be explicitly registered for aligned native columns per World. Native columns also have a finalizer fallback; explicit World disposal remains the deterministic lifetime path.
- Structural changes are immediate outside system execution and rejected during execution; workers record changes in an EntityCommandBuffer, which the World owner thread plays back afterward.
- Archetypes cache add/remove transition targets and their chunk capacity. Capacity calculation (including component registry lookups) occurs once when an archetype is created.
- Archetype transitions allocate one owned signature array; chunks locate columns through sorted component IDs instead of a per-chunk dictionary.
- Empty chunks are removed and disposed regardless of their position in the archetype chunk list. Partially filled chunks are retained to avoid relocating live component data on every removal.
- Query-builder cache keys are structural value keys rather than allocated strings; each builder stores required/excluded IDs in one lazily allocated list and reuses its built query until With/Without changes the definition.
- Archetype column creation does not capture a per-archetype factory delegate; fixed-signature reserve APIs use stack storage instead of temporary arrays.
- Typed queries use cached archetype matches and value-type enumerators. Query enumerators capture a World structural version and throw if entities/archetypes/chunks are structurally changed before enumeration finishes.
- ComponentChunk views expose version-checked component accessors, not raw Span<T> or ref T. Every index access validates the captured structural version. Entity.GetComponentReference returns a ref-struct value accessor whose Value property also checks the version; no raw component reference escapes the API.
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
        var position = positions[i];
        var velocity = velocities[i];
        position.X += velocity.X;
        position.Y += velocity.Y;
        position.Z += velocity.Z;
        positions[i] = position;
    }
}

public sealed class PlayerEntity : Entity { }
~~~

## Storage and GC

Managed arrays are the safe default. The GC can see and update every reference in reference-containing components. Native columns are opt-in and only available for unmanaged component types. Native columns are allocated through Auralis.Core.MemoryUtilities.AllocateAlignedZeroed and released with their owning chunks; a finalizer is a fallback if World disposal is missed.

The current chunk layout uses a configurable target byte value and maximum row count. Each component column is a separate typed array/allocation; this is columnar SoA across component types, not field-wise SoA within each struct. The target is a capacity heuristic, not a hard memory budget: for a class component, Unsafe.SizeOf<T>() counts the reference slot in the array, not the object header, fields, or any referenced object graph. Struct component sizes include their inline fields but not the payload of referenced objects. Query-cache creation, archetype creation, and chunk growth can allocate; prewarm known signatures before entering hot simulation. Benchmark results should guide future layout changes.

ComponentSpan and ComponentReference are version-checked ref-struct views. They do not expose raw spans or raw refs. Access after a structural mutation throws rather than silently reading moved data or dereferencing a disposed native column. Query enumeration also throws on the next MoveNext after a structural change. Avoid using unsafe pointers or retaining unmanaged addresses obtained outside these APIs.

## Transform and scripting integration

- LocalTransform, WorldTransform, and Parent are value-type components using Auralis.Core.Mathematics.
- TransformSystem uses three chunk-linear passes: cache local/parent data, resolve parent chains in reusable arrays, then write successful results. It performs one optional Parent lookup per transform entity rather than repeatedly looking up all ancestor components.
- Cycles and live parents missing transform components are non-fatal content errors. Those affected hierarchies are skipped for that update; unrelated hierarchies continue. Inspect ErrorCount, LastError, and LastErrorEntity after Execute.
- Every Script subclass added through AddComponent<ConcreteScript>, SetComponent, or AddScript is normalized into the Script base-component column, so ScriptSystem discovers it. Get/Has/TryGet/Remove using the concrete script type still resolve the stored Script instance.

## EntityCommandBuffer concurrency and failure semantics

- Recording is multi-producer and does not use an application-level lock. ConcurrentQueue plus an atomic state/writer-registration protocol ensures Clear/Playback cannot race a producer that has already registered.
- Multiple workers may safely record into one buffer. Command order between concurrent producers is not deterministic; use one buffer per worker and play them back in a fixed order when deterministic ordering matters.
- Only recording is concurrent. World structural mutation and command-buffer playback must run on the World owner thread. Playback does not hold a lock while commands execute; workers attempting to record once playback starts receive an InvalidOperationException rather than blocking behind an Entity lifecycle hook.
- A non-empty playback consumes the buffer whether it succeeds or throws. If a command fails, changes from earlier commands remain, the failed command and remaining commands are discarded, and the buffer cannot be replayed. Playback is not transactional; reconstruct a failed batch explicitly before retrying.
- Add throws if the target already has that component. Create throws if the supplied Entity is already attached or destroyed. Set replaces an existing component or adds it if missing. Remove and Destroy ignore stale entity IDs. Empty playback is a no-op and does not consume the buffer. Call Clear to discard pending commands and reuse a consumed buffer.

## Current limitations

- World structural mutation and disposal are owner-thread-only.
- The scheduler does not yet run jobs in parallel or perform work stealing. Access declarations currently describe conflicts but are not yet used to build parallel batches.
- The World itself is not thread-safe. Concurrent command recording does not make component access, queries, or World mutation thread-safe.
- Query component views are writable by default; read-only views are available, but the scheduler does not yet enforce access modes.
- EntityCommandBuffer allocates one command object per recorded operation. The producer queue itself is concurrent; it is intended for structural changes, not per-component hot-path traffic.
- Shared components, dynamic buffers, singleton components, prefabs, serialization, snapshots, replication, and source generation are future extensions.
- Partially filled chunks are not compacted across an archetype. Empty chunks are reclaimed immediately; a future compaction policy can address sustained fragmentation without moving live rows on every deletion.
- The first version prioritizes correctness and a stable extension surface over claiming unmeasured performance.
