using Auralis.Engine;
using Auralis.Engine.ECS;
using Auralis.Engine.Transform;
using Auralis.Engine.Scripting;
using Auralis.Core.Mathematics;
using Xunit;

namespace Auralis.Engine.Tests;

public sealed class WorldTests
{
    private struct Position { public float X; }
    private struct Velocity { public float X; }
    private sealed class ManagedData { public string Value { get; set; } = string.Empty; }
    private sealed class PlayerEntity : Entity { public PlayerEntity() { } }

    [Fact]
    public void EntityIsAnInheritableObjectAndOwnsRuntimeIdentity()
    {
        using var world = new World();
        var player = world.CreateEntity<PlayerEntity>("Player");
        Assert.Equal("Player", player.Name);
        Assert.NotEqual(Guid.Empty, player.Id);
        Assert.True(world.IsAlive(player.EntityId));
        Assert.Same(player, world.GetEntity(player.EntityId));
    }

    [Fact]
    public void ComponentMigrationPreservesExistingValues()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position { X = 12 });
        entity.AddComponent(new Velocity { X = 4 });

        Assert.Equal(12f, entity.GetComponent<Position>().X);
        Assert.Equal(4f, entity.GetComponent<Velocity>().X);
        Assert.Equal(3, world.ArchetypeCount);
    }

    [Fact]
    public void RemovingAComponentMigratesEntityAndPreservesRemainingData()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position { X = 7 });
        entity.AddComponent(new Velocity { X = 3 });

        Assert.True(entity.RemoveComponent<Velocity>());
        Assert.False(entity.HasComponent<Velocity>());
        Assert.Equal(7f, entity.GetComponent<Position>().X);
        Assert.False(entity.RemoveComponent<Velocity>());
    }

    [Fact]
    public void DestroyedHandlesBecomeStaleWhenSlotIsReused()
    {
        using var world = new World();
        var first = world.CreateEntity();
        var oldId = first.EntityId;
        Assert.True(first.Destroy());
        var second = world.CreateEntity();

        Assert.Equal(oldId.Index, second.EntityId.Index);
        Assert.NotEqual(oldId.Generation, second.EntityId.Generation);
        Assert.False(world.IsAlive(oldId));
        Assert.True(world.IsAlive(second.EntityId));
        Assert.True(second.Destroy());
    }

    [Fact]
    public void TypedQueriesIterateDenseComponentColumns()
    {
        using var world = new World();
        for (var i = 0; i < 100; i++)
        {
            var entity = world.CreateEntity();
            entity.AddComponent(new Position { X = i });
            if ((i & 1) == 0) entity.AddComponent(new Velocity { X = 1 });
        }

        var count = 0;
        var sum = 0f;
        foreach (var chunk in world.Query<Position, Velocity>())
        {
            var positions = chunk.First;
            var velocities = chunk.Second;
            for (var i = 0; i < chunk.Count; i++)
            {
                count++;
                sum += positions[i].X + velocities[i].X;
            }
        }

        Assert.Equal(50, count);
        Assert.Equal(2500f, sum);
    }

    [Fact]
    public void QueryEnumerationAndComponentViewsRejectStructuralMutation()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position { X = 5 });

        var enumerator = world.Query<Position>().GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var staleComponents = enumerator.Current.Components;
        var staleReference = entity.GetComponentReference<Position>();

        entity.AddComponent(new Velocity());

        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        Assert.Throws<InvalidOperationException>(() => _ = staleComponents[0].X);
        Assert.Throws<InvalidOperationException>(() => _ = staleReference.Value.X);
    }

    [Fact]
    public void ExistingComponentAccessorCanReadAndWriteWithoutExposingRawReference()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position { X = 1 });

        var accessor = entity.GetComponentReference<Position>();
        var position = accessor.Value;
        position.X = 9;
        accessor.Value = position;

        Assert.Equal(9f, entity.GetComponent<Position>().X);
    }

    [Fact]
    public void EmptyCommandBufferPlaybackDoesNotConsumeTheBuffer()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position());

        var commands = new EntityCommandBuffer();
        commands.Playback(world);
        commands.Set(entity.EntityId, new Position { X = 3 });
        commands.Playback(world);

        Assert.Equal(3f, entity.GetComponent<Position>().X);
    }

    [Fact]
    public void CommandBufferRecordingDuringDestroyHookDoesNotBlock()
    {
        using var world = new World();
        var target = world.CreateEntity();
        target.AddComponent(new Position());

        var commands = new EntityCommandBuffer();
        var destroyer = world.CreateEntity(new RecordDuringDestroyEntity(commands, target.EntityId));
        commands.Destroy(destroyer.EntityId);

        commands.Playback(world);

        Assert.True(((RecordDuringDestroyEntity)destroyer).WorkerWasRejected);
        Assert.Equal(0f, target.GetComponent<Position>().X);
    }

    [Fact]
    public void EntityNameHookCannotReenterWorldCreation()
    {
        using var world = new World();
        var entity = new ReentrantNameEntity(world);

        Assert.Throws<InvalidOperationException>(() => world.CreateEntity(entity, "Reentrant"));
        Assert.False(entity.IsAttached);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void WorldComponentChecksValidateNullAndDisposedState()
    {
        var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position());
        var id = entity.EntityId;

        Assert.Throws<ArgumentNullException>(() => world.HasComponent<Position>(null!));

        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => world.TryGetComponent<Position>(id, out _));
    }

    [Fact]
    public void QueryBuilderSupportsRequiredAndExcludedComponents()
    {
        using var world = new World();
        for (var i = 0; i < 10; i++)
        {
            var entity = world.CreateEntity();
            entity.AddComponent(new Position());
            if (i < 4) entity.AddComponent(new Velocity());
        }

        var builder = world.Query();
        builder.With<Position>().Without<Velocity>();
        var count = 0;
        foreach (var chunk in builder.Build())
            count += chunk.Count;

        Assert.Equal(6, count);
    }

    [Fact]
    public void QueryBuilderCanSwitchAComponentBetweenRequiredAndExcluded()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position());

        var builder = world.Query().With<Position>().Without<Velocity>();
        var requiredCount = 0;
        foreach (var chunk in builder.Build())
            requiredCount += chunk.Count;
        Assert.Equal(1, requiredCount);

        builder.With<Velocity>().Without<Position>();
        var excludedCount = 0;
        foreach (var chunk in builder.Build())
            excludedCount += chunk.Count;
        Assert.Equal(0, excludedCount);
    }

    [Fact]
    public void ManagedReferenceComponentsRemainGcTracked()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new ManagedData { Value = "managed" });
        GC.Collect();
        Assert.Equal("managed", entity.GetComponent<ManagedData>().Value);
    }

    [Fact]
    public void NativeStorageCanBeOptedIntoForUnmanagedComponents()
    {
        using var world = new World();
        world.RegisterUnmanagedComponent<Position>();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position { X = 42 });
        Assert.Equal(42f, entity.GetComponent<Position>().X);
    }

    [Fact]
    public void CommandBufferAppliesStructuralChangesAfterSystemExecution()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position());
        var commands = new EntityCommandBuffer();
        var scheduler = new SystemScheduler();
        var group = scheduler.CreateGroup("Simulation");
        group.Add(new SystemDescriptor("Destroy", new RecordDestroy(commands, entity.EntityId)));

        scheduler.Update(world, 1f / 60f);
        Assert.True(world.IsAlive(entity.EntityId));
        commands.Playback(world);
        Assert.False(world.IsAlive(entity.EntityId));
    }

    [Fact]
    public void CommandBufferCannotReplayCommandsAfterPartialPlaybackFailure()
    {
        using var world = new World();
        var existing = world.CreateEntity();
        existing.AddComponent(new Position());

        var created = new Entity();
        var commands = new EntityCommandBuffer();
        commands.Create(created, "Created before failure");
        commands.Add(existing.EntityId, new Position());
        commands.Destroy(existing.EntityId);

        Assert.Equal(3, commands.Count);
        Assert.Throws<InvalidOperationException>(() => commands.Playback(world));

        // The create command ran once. The failing add stopped playback, and the
        // later destroy was discarded with the rest of the consumed command list.
        Assert.True(created.IsAttached);
        Assert.True(world.IsAlive(existing.EntityId));
        Assert.Equal(2, world.EntityCount);
        Assert.Equal(0, commands.Count);

        Assert.Throws<InvalidOperationException>(() => commands.Playback(world));
        Assert.Equal(2, world.EntityCount);

        // Explicitly clearing a consumed buffer is the only way to reuse it.
        commands.Clear();
        commands.Destroy(existing.EntityId);
        commands.Playback(world);
        Assert.False(world.IsAlive(existing.EntityId));
    }

    [Fact]
    public void CommandBufferSupportsConcurrentRecordingFromWorkers()
    {
        using var world = new World();
        const int entityCount = 128;
        var entities = new Entity[entityCount];
        var ids = new EntityId[entityCount];

        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.CreateEntity();
            entities[i].AddComponent(new Position());
            ids[i] = entities[i].EntityId;
        }

        var commands = new EntityCommandBuffer();
        Parallel.For(0, entityCount, i =>
            commands.Set(ids[i], new Position { X = i + 1 }));

        Assert.Equal(entityCount, commands.Count);

        // Playback mutates World and therefore runs on the World owner thread.
        commands.Playback(world);

        for (var i = 0; i < entityCount; i++)
            Assert.Equal(i + 1, entities[i].GetComponent<Position>().X);
        Assert.Equal(0, commands.Count);
    }

    [Fact]
    public void CommandBufferPlaybackMustRunOnWorldOwnerThread()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        entity.AddComponent(new Position());

        var commands = new EntityCommandBuffer();
        commands.Set(entity.EntityId, new Position { X = 42 });

        Assert.Throws<InvalidOperationException>(() =>
            Task.Factory.StartNew(
                () => commands.Playback(world),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).GetAwaiter().GetResult());

        // Rejected playback must leave the queued commands available to the owner.
        Assert.Equal(1, commands.Count);
        Assert.Equal(0f, entity.GetComponent<Position>().X);

        commands.Playback(world);
        Assert.Equal(42f, entity.GetComponent<Position>().X);
    }

    [Fact]
    public void TransformSystemResolvesParentCreatedAfterChild()
    {
        using var world = new World();

        var child = world.CreateEntity();
        child.AddComponent(new LocalTransform(new Vector3D(2, 0, 0), Quaternion.Identity, Vector3D.One));
        child.AddComponent(new WorldTransform());
        child.AddComponent(new Parent());

        var parent = world.CreateEntity();
        parent.AddComponent(new LocalTransform(new Vector3D(10, 0, 0), Quaternion.Identity, Vector3D.One));
        parent.AddComponent(new WorldTransform());
        parent.AddComponent(new Parent());

        // Both entities occupy the same chunk, with the child first.
        child.SetComponent(new Parent(parent.EntityId));

        new TransformSystem().Execute(world, 0);
        Assert.Equal(12f, child.GetComponent<WorldTransform>().Matrix.TranslationVector.X);
    }

    [Fact]
    public void TransformSystemReportsParentCyclesWithoutAbortingTheUpdate()
    {
        using var world = new World();
        var first = world.CreateEntity();
        var second = world.CreateEntity();
        var independentRoot = world.CreateEntity();

        first.AddComponent(LocalTransform.Identity);
        first.AddComponent(new WorldTransform());
        first.AddComponent(new Parent());
        second.AddComponent(LocalTransform.Identity);
        second.AddComponent(new WorldTransform());
        second.AddComponent(new Parent());
        independentRoot.AddComponent(new LocalTransform(new Vector3D(7, 0, 0), Quaternion.Identity, Vector3D.One));
        independentRoot.AddComponent(new WorldTransform());

        first.SetComponent(new Parent(second.EntityId));
        second.SetComponent(new Parent(first.EntityId));

        var system = new TransformSystem();
        system.Execute(world, 0);

        Assert.Equal(1, system.ErrorCount);
        Assert.Equal(TransformHierarchyError.CycleDetected, system.LastError);
        Assert.Equal(7f, independentRoot.GetComponent<WorldTransform>().Matrix.TranslationVector.X);
    }

    [Fact]
    public void TransformSystemSkipsEntitiesWithParentsMissingTransformComponents()
    {
        using var world = new World();
        var parent = world.CreateEntity(); // Alive, but deliberately has no transform components.
        var child = world.CreateEntity();
        child.AddComponent(LocalTransform.Identity);
        child.AddComponent(new WorldTransform());
        child.AddComponent(new Parent(parent.EntityId));
        var independentRoot = world.CreateEntity();
        independentRoot.AddComponent(new LocalTransform(new Vector3D(4, 0, 0), Quaternion.Identity, Vector3D.One));
        independentRoot.AddComponent(new WorldTransform());

        var system = new TransformSystem();
        system.Execute(world, 0);

        Assert.Equal(1, system.ErrorCount);
        Assert.Equal(TransformHierarchyError.ParentMissingTransform, system.LastError);
        Assert.Equal(4f, independentRoot.GetComponent<WorldTransform>().Matrix.TranslationVector.X);
    }

    [Fact]
    public void EntityLifecycleHookRunsOnDestroy()
    {
        using var world = new World();
        var entity = world.CreateEntity<LifecycleEntity>();
        Assert.False(entity.DestroyedByHook);
        entity.Destroy();
        Assert.True(entity.DestroyedByHook);
    }

    [Fact]
    public void ScriptSystemFindsConcreteScriptsAddedThroughAddComponent()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        var script = entity.AddComponent(new CountingScript());

        new ScriptSystem().Execute(world, 0.25f);

        Assert.Equal(0.25f, script.Elapsed);
        Assert.True(entity.HasComponent<CountingScript>());
        Assert.Same(script, entity.GetComponent<CountingScript>());
        Assert.True(entity.RemoveComponent<CountingScript>());
        Assert.False(entity.HasComponent<Script>());
    }

    private sealed class LifecycleEntity : Entity
    {
        public LifecycleEntity() { }
        public bool DestroyedByHook { get; private set; }
        protected override void OnDestroy() => DestroyedByHook = true;
    }

    private sealed class ReentrantNameEntity(World world) : Entity
    {
        protected override void OnNameChanged() => world.CreateEntity();
    }

    private sealed class RecordDuringDestroyEntity(EntityCommandBuffer commands, EntityId target) : Entity
    {
        public bool WorkerWasRejected { get; private set; }

        protected override void OnDestroy()
        {
            var worker = Task.Factory.StartNew(
                () => commands.Set(target, new Position { X = 99 }),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Worker was blocked while command playback was executing.");
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                WorkerWasRejected = true;
            }
        }
    }

    private sealed class CountingScript : Script
    {
        public float Elapsed { get; private set; }
        public override void Update(float deltaTime) => Elapsed += deltaTime;
    }

    private sealed class RecordDestroy(EntityCommandBuffer commands, EntityId entity) : ISystem
    {
        public void Execute(World world, float deltaTime) => commands.Destroy(entity);
    }
}
