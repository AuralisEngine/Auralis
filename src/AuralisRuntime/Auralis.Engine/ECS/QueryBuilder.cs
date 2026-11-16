namespace Auralis.Engine.ECS;

/// <summary>Builds an entity query from required and excluded component types.</summary>
public sealed class QueryBuilder
{
    private readonly World _world;
    private readonly List<int> _include = new();
    private readonly List<int> _exclude = new();

    internal QueryBuilder(World world) => _world = world;

    public QueryBuilder With<T>() where T : notnull
    {
        var id = ComponentType<T>.Id;
        if (!_include.Contains(id)) _include.Add(id);
        _exclude.Remove(id);
        return this;
    }

    public QueryBuilder Without<T>() where T : notnull
    {
        var id = ComponentType<T>.Id;
        if (!_exclude.Contains(id)) _exclude.Add(id);
        _include.Remove(id);
        return this;
    }

    public EntityQuery Build() => _world.BuildQuery(_include.ToArray(), _exclude.ToArray());
}
