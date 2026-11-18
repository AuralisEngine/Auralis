namespace Auralis.Engine.ECS;

/// <summary>One simulation system executed by a World scheduler.</summary>
public interface ISystem
{
    void Execute(World world, float deltaTime);
}

/// <summary>Describes how a system accesses one component type.</summary>
public readonly record struct ComponentAccess(int ComponentTypeId, ComponentAccessMode Mode)
{
    public static ComponentAccess Read<T>() where T : notnull =>
        new(ComponentType<T>.Id, ComponentAccessMode.Read);

    public static ComponentAccess Write<T>() where T : notnull =>
        new(ComponentType<T>.Id, ComponentAccessMode.Write);
}

public enum ComponentAccessMode
{
    Read,
    Write
}
