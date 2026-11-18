namespace Auralis.Engine.ECS;

internal readonly record struct EntityLocation(Archetype Archetype, Chunk Chunk, int Index);
