using System.Buffers;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>
/// A parsed FBX document. The document owns its node/property tree; the input memory is not retained.
/// </summary>
public sealed class FBXDocument
{
    private static readonly byte[] BinaryMagic = System.Text.Encoding.ASCII.GetBytes(FBXHeader.BinarySignature);
    private readonly List<FBXNode> _roots;

    public FBXHeader Header { get; }
    public IReadOnlyList<FBXNode> RootNodes => _roots;

    private FBXDocument(FBXHeader header, List<FBXNode> roots)
    {
        Header = header;
        _roots = roots;
    }

    public FBXNode? GetRootNode(string name)
    {
        foreach (FBXNode node in _roots)
            if (string.Equals(node.Name, name, StringComparison.Ordinal))
                return node;
        return null;
    }

    public static FBXDocument Parse(ReadOnlyMemory<byte> data, FBXParserOptions? options = null)
    {
        options ??= new FBXParserOptions();
        options.Validate();
        if (data.Length == 0)
            throw new FBXFormatException("The FBX input is empty.");
        if (data.Length > options.MaxFileBytes)
            throw new FBXFormatException($"The FBX input exceeds the configured {options.MaxFileBytes}-byte limit.");

        return data.Span.StartsWith(BinaryMagic)
            ? FBXBinaryParser.Parse(data, options)
            : FBXAsciiParser.Parse(data, options);
    }

    public static FBXDocument ParseBinary(ReadOnlyMemory<byte> data, FBXParserOptions? options = null)
    {
        options ??= new FBXParserOptions();
        options.Validate();
        if (!data.Span.StartsWith(BinaryMagic))
            throw new FBXFormatException("The input does not have the binary FBX signature.");
        return FBXBinaryParser.Parse(data, options);
    }

    public static FBXDocument ParseText(ReadOnlyMemory<byte> data, FBXParserOptions? options = null)
    {
        options ??= new FBXParserOptions();
        options.Validate();
        if (data.Span.StartsWith(BinaryMagic))
            throw new FBXFormatException("A binary FBX document was supplied to the ASCII parser.");
        return FBXAsciiParser.Parse(data, options);
    }

    public static ValueTask<FBXDocument> ParseAsync(ReadOnlyMemory<byte> data, FBXParserOptions? options = null) =>
        ValueTask.FromResult(Parse(data, options));

    public static ValueTask<FBXDocument> ParseBinaryAsync(ReadOnlyMemory<byte> data, FBXParserOptions? options = null) =>
        ValueTask.FromResult(ParseBinary(data, options));

    public static ValueTask<FBXDocument> ParseTextAsync(ReadOnlyMemory<byte> data, FBXParserOptions? options = null) =>
        ValueTask.FromResult(ParseText(data, options));

    public static async ValueTask<FBXDocument> ParseAsync(Stream stream, FBXParserOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(stream));

        options ??= new FBXParserOptions();
        options.Validate();

        using var buffer = new MemoryStream();
        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > options.MaxFileBytes)
                    throw new FBXFormatException($"The FBX stream exceeds the configured {options.MaxFileBytes}-byte limit.");
                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (!buffer.TryGetBuffer(out ArraySegment<byte> segment))
                throw new InvalidOperationException("The internal FBX stream buffer is unavailable.");
            return Parse(new ReadOnlyMemory<byte>(segment.Array!, segment.Offset, checked((int)buffer.Length)), options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static async ValueTask<FBXDocument> ParseBinaryAsync(Stream stream, FBXParserOptions? options = null, CancellationToken cancellationToken = default)
    {
        FBXDocument document = await ParseAsync(stream, options, cancellationToken).ConfigureAwait(false);
        if (!document.Header.IsBinary)
            throw new FBXFormatException("The stream contains an ASCII FBX document, not a binary FBX document.");
        return document;
    }

    public static async ValueTask<FBXDocument> ParseTextAsync(Stream stream, FBXParserOptions? options = null, CancellationToken cancellationToken = default)
    {
        FBXDocument document = await ParseAsync(stream, options, cancellationToken).ConfigureAwait(false);
        if (document.Header.IsBinary)
            throw new FBXFormatException("The stream contains a binary FBX document, not an ASCII FBX document.");
        return document;
    }

    internal static FBXDocument Create(FBXHeader header, List<FBXNode> roots) => new(header, roots);
}
