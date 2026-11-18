using Auralis.Core.Mathematics;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace Auralis.Engine.Transform;

/// <summary>Compact local-space transform data stored directly in archetype columns.</summary>
public struct LocalTransform
{
    public Vector3D Position;
    public Quaternion Rotation;
    public Vector3D Scale;

    public LocalTransform(Vector3D position, Quaternion rotation, Vector3D scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public static LocalTransform Identity =>
        new(Vector3D.Zero, Quaternion.Identity, Vector3D.One);

    internal Matrix ToMatrix()
    {
        var value = Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateTranslation(Position);
        return value;
    }
}
