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
    private sealed class PlayerEntity : Entity { public PlayerEntity() { } public int Score { get; set; } }

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
    public void DetachedTransformIsAdoptedWhenEntityEntersAWorld()
    {
        using var world = new World();
        var entity = new Entity();
        var transform = entity.Transform;
        world.CreateEntity(entity);
        Assert.Same(transform, entity.Transform);
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
    public void QueryBuilderSupportsRequiredAndExcludedComponents()
    {
        using var world = new World();
        for (var i = 0; i < 10; i++)
        {
            var entity = world.CreateEntity();
            entity.AddComponent(new Position());
            if (i < 4) entity.AddComponent(new Velocity());
        }

        var count = 0;
        foreach (var chunk in world.Query().With<Position>().Without<Velocity>().Build())
            count += chunk.Count;

        Assert.Equal(6, count);
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
    public void TransformSystemResolvesParentBeforeChild()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        parent.AddComponent(new LocalTransform(new Vector3D(10, 0, 0), Quaternion.Identity, Vector3D.One));
        parent.AddComponent(new WorldTransform());

        var child = world.CreateEntity();
        child.AddComponent(new LocalTransform(new Vector3D(2, 0, 0), Quaternion.Identity, Vector3D.One));
        child.AddComponent(new WorldTransform());
        child.AddComponent(new Parent(parent.EntityId));

        new TransformSystem().Execute(world, 0);
        Assert.Equal(12f, child.GetComponent<WorldTransform>().Matrix.TranslationVector.X);
    }


    [Fact]
    public void EntityLifecycleHookRunsOnDestroy()
    {
        using var world = new World();
        var entity = world.CreateEntity<LifecycleEntity>();
        Assert.True(entity.DestroyedByHook is false);
        entity.Destroy();
        Assert.True(entity.DestroyedByHook);
    }

    [Fact]
    public void ScriptSystemExecutesManagedScriptComponents()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        var script = entity.AddScript(new CountingScript());
        new ScriptSystem().Execute(world, 0.25f);
        Assert.Equal(0.25f, script.Elapsed);
    }

    private sealed class LifecycleEntity : Entity
    {
        public LifecycleEntity() { }
        public bool DestroyedByHook { get; private set; }
        protected override void OnDestroy() => DestroyedByHook = true;
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
