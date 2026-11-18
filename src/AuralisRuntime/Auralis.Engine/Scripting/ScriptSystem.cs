using Auralis.Engine;
using Auralis.Engine.ECS;

namespace Auralis.Engine.Scripting;

/// <summary>
/// Executes all Script instances. World normalizes concrete Script subclasses into
/// the Script base-component column when they are added or set.
/// </summary>
public sealed class ScriptSystem : ISystem
{
    public void Execute(World world, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Query<Script>())
        {
            var scripts = chunk.Components;
            for (var i = 0; i < chunk.Count; i++)
                scripts[i].Update(deltaTime);
        }
    }
}
