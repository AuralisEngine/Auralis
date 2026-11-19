namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>Header metadata common to binary and ASCII FBX files.</summary>
public sealed class FBXHeader
{
    public const string BinarySignature = "Kaydara FBX Binary  \0\u001a\0";

    public bool IsBinary { get; }
    public uint Version { get; }
    public byte[] Magic { get; }

    internal FBXHeader(bool isBinary, uint version, byte[]? magic = null)
    {
        IsBinary = isBinary;
        Version = version;
        Magic = magic ?? Array.Empty<byte>();
    }

    public override string ToString() =>
        $"FBX {Version / 1000}.{(Version % 1000) / 100}.{(Version % 100) / 10} ({(IsBinary ? "Binary" : "ASCII")})";
}
