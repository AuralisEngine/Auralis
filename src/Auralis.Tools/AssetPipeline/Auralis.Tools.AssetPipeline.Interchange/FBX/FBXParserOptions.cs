namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>Resource limits applied while parsing untrusted FBX files.</summary>
public sealed record FBXParserOptions
{
    public int MaxFileBytes { get; init; } = 512 * 1024 * 1024;
    public int MaxNodes { get; init; } = 2_000_000;
    public int MaxDepth { get; init; } = 256;
    public int MaxPropertiesPerNode { get; init; } = 1_000_000;
    public int MaxArrayElements { get; init; } = 100_000_000;
    public int MaxArrayBytes { get; init; } = 512 * 1024 * 1024;
    public int MaxStringBytes { get; init; } = 16 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxFileBytes <= 0 || MaxNodes <= 0 || MaxDepth <= 0 ||
            MaxPropertiesPerNode <= 0 || MaxArrayElements <= 0 || MaxArrayBytes <= 0 || MaxStringBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(FBXParserOptions), "All parser limits must be positive.");
    }
}
