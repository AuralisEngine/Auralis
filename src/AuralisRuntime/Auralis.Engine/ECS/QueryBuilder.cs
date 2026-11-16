namespace Auralis.Engine.ECS;

/// <summary>Builds an entity query from required and excluded component types.</summary>
public sealed class QueryBuilder
{
    private readonly World _world;
    private readonly List<int> _include = new();
    private readonly List<int> _exclude = new();
    private EntityQuery _builtQuery;
    private bool _hasBuiltQuery;

    internal QueryBuilder(World world) => _world = world;

    public QueryBuilder With<T>() where T : notnull
    {
        var id = ComponentType<T>.Id;
        if (!_include.Contains(id)) _include.Add(id);
        _exclude.Remove(id);
        _hasBuiltQuery = false;
        return this;
    }

    public QueryBuilder Without<T>() where T : notnull
    {
        var id = ComponentType<T>.Id;
        if (!_exclude.Contains(id)) _exclude.Add(id);
        _include.Remove(id);
        _hasBuiltQuery = false;
        return this;
    }

    public EntityQuery Build()
    {
        _world.ThrowIfDisposed();
        if (_hasBuiltQuery) return _builtQuery;
        _builtQuery = _world.BuildQuery(_include.ToArray(), _exclude.ToArray());
        _hasBuiltQuery = true;
        return _builtQuery;
    }
}
