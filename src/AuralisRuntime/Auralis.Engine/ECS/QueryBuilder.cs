namespace Auralis.Engine.ECS;

/// <summary>Builds an entity query from required and excluded component types.</summary>
public sealed class QueryBuilder
{
    private readonly World _world;
    // Positive IDs are required; negative IDs are excluded. A single list avoids
    // keeping two List instances and their separate backing arrays for each builder.
    private List<int>? _terms;
    private EntityQuery _builtQuery;
    private bool _hasBuiltQuery;

    internal QueryBuilder(World world) => _world = world;

    public QueryBuilder With<T>() where T : notnull
    {
        var id = ComponentType<T>.Id;
        var terms = _terms ??= new List<int>();

        for (var i = 0; i < terms.Count; i++)
        {
            if (terms[i] == id)
                return this;

            if (terms[i] == -id)
            {
                terms[i] = id;
                _hasBuiltQuery = false;
                return this;
            }
        }

        terms.Add(id);
        _hasBuiltQuery = false;
        return this;
    }

    public QueryBuilder Without<T>() where T : notnull
    {
        var id = ComponentType<T>.Id;
        var terms = _terms ??= new List<int>();

        for (var i = 0; i < terms.Count; i++)
        {
            if (terms[i] == -id)
                return this;

            if (terms[i] == id)
            {
                terms[i] = -id;
                _hasBuiltQuery = false;
                return this;
            }
        }

        terms.Add(-id);
        _hasBuiltQuery = false;
        return this;
    }

    public EntityQuery Build()
    {
        _world.ThrowIfDisposed();
        if (_hasBuiltQuery)
            return _builtQuery;

        if (_terms is null || _terms.Count == 0)
        {
            _builtQuery = _world.BuildQuery(Array.Empty<int>(), Array.Empty<int>());
            _hasBuiltQuery = true;
            return _builtQuery;
        }

        var includeCount = 0;
        foreach (var term in _terms)
            if (term > 0)
                includeCount++;

        var excludeCount = _terms.Count - includeCount;
        var include = includeCount == 0 ? Array.Empty<int>() : new int[includeCount];
        var exclude = excludeCount == 0 ? Array.Empty<int>() : new int[excludeCount];

        var includeIndex = 0;
        var excludeIndex = 0;
        foreach (var term in _terms)
        {
            if (term > 0)
                include[includeIndex++] = term;
            else
                exclude[excludeIndex++] = -term;
        }

        _builtQuery = _world.BuildQuery(include, exclude);
        _hasBuiltQuery = true;
        return _builtQuery;
    }
}
