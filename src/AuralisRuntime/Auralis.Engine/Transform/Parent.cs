using Auralis.Engine.ECS;

namespace Auralis.Engine.Transform;

/// <summary>Optional parent relationship. A default value represents a root entity.</summary>
public struct Parent
{
    public EntityId Entity;

    public Parent(EntityId entity) => Entity = entity;
    public bool HasParent => Entity.IsValid;
}
