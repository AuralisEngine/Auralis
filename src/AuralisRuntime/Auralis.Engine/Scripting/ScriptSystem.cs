using Auralis.Engine;
using Auralis.Engine.ECS;

namespace Auralis.Engine.Scripting;

/// <summary>Executes Script instances stored explicitly as the Script base component type.</summary>
public sealed class ScriptSystem : ISystem
{
    public void Execute(World world, float deltaTime)
    {
        foreach (var chunk in world.Query<Script>())
        {
            var scripts = chunk.Components;
            for (var i = 0; i < chunk.Count; i++)
                scripts[i].Update(deltaTime);
        }
    }
}
