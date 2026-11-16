namespace Auralis.Engine.ECS;

/// <summary>Registration metadata for a system and its declared component access.</summary>
public sealed class SystemDescriptor
{
    public string Name { get; }
    public ISystem System { get; }
    public IReadOnlyList<ComponentAccess> Accesses { get; }

    public SystemDescriptor(string name, ISystem system, params ComponentAccess[] accesses)
    {
        System = system ?? throw new ArgumentNullException(nameof(system));
        Name = string.IsNullOrWhiteSpace(name) ? System.GetType().Name : name;
        Accesses = accesses is null ? Array.Empty<ComponentAccess>() : (ComponentAccess[])accesses.Clone();
    }

    public bool ConflictsWith(SystemDescriptor other)
    {
        foreach (var left in Accesses)
            foreach (var right in other.Accesses)
                if (left.ComponentTypeId == right.ComponentTypeId &&
                    (left.Mode == ComponentAccessMode.Write || right.Mode == ComponentAccessMode.Write))
                    return true;
        return false;
    }
}
