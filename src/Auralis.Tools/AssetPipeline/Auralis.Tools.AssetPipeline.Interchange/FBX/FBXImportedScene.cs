using System.Numerics;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>
/// In-memory result of importing an FBX scene. This is an interchange data model, not the
/// serialized .AuraAsset format; serialization is intentionally handled by a separate pipeline stage.
/// </summary>
public sealed class FBXImportedScene
{
    public string Name { get; internal set; } = "Scene";
    public IReadOnlyList<FBXImportedNode> Nodes { get; internal set; } = Array.Empty<FBXImportedNode>();
    public IReadOnlyList<FBXImportedMesh> Meshes { get; internal set; } = Array.Empty<FBXImportedMesh>();
    public IReadOnlyList<FBXImportedMaterial> Materials { get; internal set; } = Array.Empty<FBXImportedMaterial>();
    public IReadOnlyList<FBXImportedTexture> Textures { get; internal set; } = Array.Empty<FBXImportedTexture>();
    public IReadOnlyList<FBXImportedSkeleton> Skeletons { get; internal set; } = Array.Empty<FBXImportedSkeleton>();
    public IReadOnlyList<FBXImportedAnimationClip> Animations { get; internal set; } = Array.Empty<FBXImportedAnimationClip>();
    public IReadOnlyList<FBXImportDiagnostic> Diagnostics { get; internal set; } = Array.Empty<FBXImportDiagnostic>();
}

public sealed class FBXImportedNode
{
    public long SourceId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public string AttributeType { get; internal set; } = string.Empty;
    public int ParentIndex { get; internal set; } = -1;
    public IReadOnlyList<int> Children { get; internal set; } = Array.Empty<int>();
    public Vector3 Translation { get; internal set; }
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;
    public Vector3 Scale { get; internal set; } = Vector3.One;
    public Matrix4x4 LocalTransform { get; internal set; } = Matrix4x4.Identity;
    /// <summary>Non-inherited geometry offset applied to this node's mesh attribute.</summary>
    public Matrix4x4 GeometryTransform { get; internal set; } = Matrix4x4.Identity;
    public Matrix4x4 WorldTransform { get; internal set; } = Matrix4x4.Identity;
    public int MeshIndex { get; internal set; } = -1;
    public IReadOnlyList<int> MeshIndices { get; internal set; } = Array.Empty<int>();
    public IReadOnlyList<int> MaterialIndices { get; internal set; } = Array.Empty<int>();
}

public sealed class FBXImportedMesh
{
    public long SourceId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    /// <summary>Positions are in Unreal-style centimeters and axes (X forward, Y right, Z up).</summary>
    public Vector3[] Positions { get; internal set; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; internal set; } = Array.Empty<Vector3>();
    /// <summary>XYZ is the tangent direction and W is its handedness sign.</summary>
    public Vector4[] Tangents { get; internal set; } = Array.Empty<Vector4>();
    /// <summary>One UV array per imported FBX UV layer.</summary>
    public Vector2[][] TextureCoordinates { get; internal set; } = Array.Empty<Vector2[]>();
    public Vector4[] Colors { get; internal set; } = Array.Empty<Vector4>();
    /// <summary>Triangle indices. The importer emits triangles and corrects winding during handedness conversion.</summary>
    public int[] Indices { get; internal set; } = Array.Empty<int>();
    public int[] SourceControlPointIndices { get; internal set; } = Array.Empty<int>();
    public int ControlPointCount { get; internal set; }
    internal Vector3[] SourceControlPoints { get; set; } = Array.Empty<Vector3>();
    public FBXImportedSubmesh[] Submeshes { get; internal set; } = Array.Empty<FBXImportedSubmesh>();
    public FBXVertexInfluence[][] BoneInfluences { get; internal set; } = Array.Empty<FBXVertexInfluence[]>();
    public FBXImportedSkeleton? Skeleton { get; internal set; }
    public FBXImportedBlendShape[] BlendShapes { get; internal set; } = Array.Empty<FBXImportedBlendShape>();
}

public readonly record struct FBXImportedSubmesh(int StartIndex, int IndexCount, int MaterialIndex);
public readonly record struct FBXVertexInfluence(int BoneIndex, float Weight);

public sealed class FBXImportedMaterial
{
    public long SourceId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public Vector3 BaseColor { get; internal set; } = Vector3.One;
    public Vector3 SpecularColor { get; internal set; } = new(0.04f);
    public Vector3 EmissiveColor { get; internal set; }
    public float Metallic { get; internal set; }
    public float Roughness { get; internal set; } = 0.5f;
    public float Opacity { get; internal set; } = 1f;
    /// <summary>Texture slots map FBX material property names to scene texture indices.</summary>
    public IReadOnlyDictionary<string, int> TextureSlots { get; internal set; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed class FBXImportedTexture
{
    public long SourceId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public string FileName { get; internal set; } = string.Empty;
    public string RelativeFileName { get; internal set; } = string.Empty;
    public byte[]? EmbeddedContent { get; internal set; }
}

public sealed class FBXImportedSkeleton
{
    public string Name { get; internal set; } = string.Empty;
    public IReadOnlyList<FBXImportedBone> Bones { get; internal set; } = Array.Empty<FBXImportedBone>();
}

public sealed class FBXImportedBone
{
    public string Name { get; internal set; } = string.Empty;
    public int NodeIndex { get; internal set; } = -1;
    public int ParentBoneIndex { get; internal set; } = -1;
    public Matrix4x4 InverseBindMatrix { get; internal set; } = Matrix4x4.Identity;
}

public sealed class FBXImportedBlendShape
{
    public long SourceId { get; internal set; }
    public long ChannelId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public float FullWeight { get; internal set; } = 100f;
    /// <summary>Per-imported-vertex position deltas; zero for vertices not affected by this target.</summary>
    public Vector3[] PositionDeltas { get; internal set; } = Array.Empty<Vector3>();
    public Vector3[] NormalDeltas { get; internal set; } = Array.Empty<Vector3>();
}

public sealed class FBXImportedAnimationClip
{
    public long SourceId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public double DurationSeconds { get; internal set; }
    public IReadOnlyList<FBXImportedAnimationTrack> Tracks { get; internal set; } = Array.Empty<FBXImportedAnimationTrack>();
}

public sealed class FBXImportedAnimationTrack
{
    public int NodeIndex { get; internal set; } = -1;
    public FBXVectorKeyframe[] TranslationKeys { get; internal set; } = Array.Empty<FBXVectorKeyframe>();
    public FBXQuaternionKeyframe[] RotationKeys { get; internal set; } = Array.Empty<FBXQuaternionKeyframe>();
    public FBXVectorKeyframe[] ScaleKeys { get; internal set; } = Array.Empty<FBXVectorKeyframe>();
}

public readonly record struct FBXVectorKeyframe(double TimeSeconds, Vector3 Value);
public readonly record struct FBXQuaternionKeyframe(double TimeSeconds, Quaternion Value);

public readonly record struct FBXImportDiagnostic(FBXDiagnosticSeverity Severity, string Message, long SourceId = 0);

public enum FBXDiagnosticSeverity : byte
{
    Info,
    Warning,
    Error
}

public sealed record FBXImporterOptions
{
    public bool GenerateMissingNormals { get; init; } = true;
    public bool GenerateMissingTangents { get; init; } = true;
    public bool ImportSkinning { get; init; } = true;
    public bool ImportAnimation { get; init; } = true;
    public bool ImportBlendShapes { get; init; } = true;
    public bool ImportMaterials { get; init; } = true;
    public bool ImportTextures { get; init; } = true;
    public int MaxImportedVertices { get; init; } = 20_000_000;
    public int MaxImportedPolygons { get; init; } = 20_000_000;
    public int MaxPolygonVertices { get; init; } = 4_096;
}
