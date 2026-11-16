using Auralis.Core.Mathematics;

namespace Auralis.Engine.Transform;

/// <summary>Cached world-space matrix and transform update stamp.</summary>
public struct WorldTransform
{
    public Matrix Matrix;
    internal ulong UpdateVersion;
}
