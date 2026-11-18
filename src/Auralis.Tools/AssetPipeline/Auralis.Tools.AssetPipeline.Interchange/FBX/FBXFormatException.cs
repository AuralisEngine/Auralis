namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>Thrown when an FBX document is malformed, unsupported, or exceeds parser limits.</summary>
public sealed class FBXFormatException : FormatException
{
    public long Offset { get; }

    public FBXFormatException(string message, long offset = -1, Exception? innerException = null)
        : base(offset >= 0 ? $"{message} (offset {offset})." : message, innerException)
    {
        Offset = offset;
    }
}
