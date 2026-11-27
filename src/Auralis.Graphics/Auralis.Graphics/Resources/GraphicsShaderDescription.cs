namespace Auralis.Graphics.Resources;

/// <summary>Shader execution stage.</summary>
public enum GraphicsShaderStage
{
    Vertex,
    Fragment,
    Compute,
    Geometry,
    TessellationControl,
    TessellationEvaluation,
    Task,
    Mesh,
    RayGeneration,
    Miss,
    ClosestHit,
    AnyHit,
    Intersection,
    Callable
}

/// <summary>
/// Identifies a shader artifact format. Known formats are conveniences; custom identifiers allow
/// backend-specific compiled artifacts without exposing native binding types in common interfaces.
/// </summary>
public readonly record struct GraphicsShaderFormat(string Identifier)
{
    public static GraphicsShaderFormat Dxil { get; } = new("dxil");
    public static GraphicsShaderFormat SpirV { get; } = new("spirv");
    public static GraphicsShaderFormat MetalLibrary { get; } = new("metal-library");
    public static GraphicsShaderFormat MetalSource { get; } = new("metal-source");
    public static GraphicsShaderFormat HlslSource { get; } = new("hlsl-source");
    public static GraphicsShaderFormat GlslSource { get; } = new("glsl-source");

    public bool IsValid => !string.IsNullOrWhiteSpace(Identifier);
}

/// <summary>Describes an already-compiled shader artifact. Compilation is deliberately outside this library.</summary>
public sealed record GraphicsShaderDescription
{
    public required GraphicsShaderStage Stage { get; init; }

    public required GraphicsShaderFormat Format { get; init; }

    public required ReadOnlyMemory<byte> Code { get; init; }

    public string EntryPoint { get; init; } = "main";

    public void Validate()
    {
        if (!Enum.IsDefined(Stage))
            throw new ArgumentOutOfRangeException(nameof(Stage));
        if (!Format.IsValid)
            throw new ArgumentException("A shader format identifier is required.", nameof(Format));
        if (Code.IsEmpty)
            throw new ArgumentException("Shader code cannot be empty.", nameof(Code));
        if (string.IsNullOrWhiteSpace(EntryPoint))
            throw new ArgumentException("A shader entry point is required.", nameof(EntryPoint));
    }
}
