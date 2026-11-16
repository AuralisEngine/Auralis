using System;
using System.Collections.Generic;
using System.Text;

namespace Auralis.Engine;

[Obsolete("Use ECS component data stored through Entity.AddComponent<T> instead.")]
public abstract class EntityComponent
{
    public Entity Entity { get; internal set; } = null!;

    public Guid Id { get; set; } = Guid.NewGuid();
}
