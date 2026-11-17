namespace Auralis.Engine.ECS;

/// <summary>Caches archetypes matching an include/exclude query signature.</summary>
internal sealed class QueryCache
{
    private readonly World _world;
    private readonly int[] _include;
    private readonly int[] _exclude;
    private readonly List<Archetype> _matches = new();
    private int _observedVersion = -1;

    internal World World => _world;

    internal IReadOnlyList<Archetype> Matches
    {
        get { Refresh(); return _matches; }
    }

    internal QueryCache(World world, int[] include, int[] exclude)
    {
        _world = world;
        _include = include;
        _exclude = exclude;
    }

    internal void Refresh()
    {
        if (_observedVersion == _world.ArchetypeVersion) return;
        _matches.Clear();
        foreach (var archetype in _world.Archetypes)
        {
            var matches = true;
            foreach (var id in _include)
                if (!archetype.Signature.Contains(id)) { matches = false; break; }
            if (!matches) continue;
            foreach (var id in _exclude)
                if (archetype.Signature.Contains(id)) { matches = false; break; }
            if (matches) _matches.Add(archetype);
        }
        _observedVersion = _world.ArchetypeVersion;
    }
}
