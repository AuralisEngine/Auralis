namespace Auralis.Engine.ECS;

/// <summary>
/// Deterministic, single-threaded scheduler with declared access metadata.
/// The metadata is the basis for a future parallel scheduler; this implementation does not
/// claim to execute systems concurrently.
/// </summary>
public sealed class SystemScheduler
{
    private readonly List<SystemGroup> _groups = new();

    public IReadOnlyList<SystemGroup> Groups => _groups;

    public SystemGroup CreateGroup(string name)
    {
        var group = new SystemGroup(name);
        _groups.Add(group);
        return group;
    }

    public void Update(World world, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.BeginSystemExecution();

        try
        {
            foreach (var group in _groups)
            {
                // Indexing avoids the interface-based enumerator from IReadOnlyList<T>.
                for (var i = 0; i < group.SystemCount; i++)
                    group.GetSystem(i).System.Execute(world, deltaTime);
            }
        }
        finally
        {
            world.EndSystemExecution();
        }
    }
}
