using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

internal static class FBXAsciiParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static FBXDocument Parse(ReadOnlyMemory<byte> data, FBXParserOptions options)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            bytes = bytes[3..];

        try
        {
            // Validate without materializing a UTF-16 copy of the complete ASCII FBX file.
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FBXFormatException("ASCII FBX input is not valid UTF-8.", 0, ex);
        }

        uint version = ReadVersion(bytes);
        var parser = new Parser(bytes, options);
        List<FBXNode> roots = parser.ReadDocument();

        if (!roots.Any(static node => node.Name is "FBXHeaderExtension" or "GlobalSettings" or
            "Documents" or "Definitions" or "Objects" or "Connections" or "Takes"))
            throw new FBXFormatException("The text input does not contain a recognizable ASCII FBX section.");

        if (version == 0)
        {
            long headerVersion = roots.FirstOrDefault(static node => node.Name == "FBXHeaderExtension")
                ?.GetChild("FBXVersion")?.GetInt64(0) ?? 0;
            if (headerVersion > 0 && headerVersion <= uint.MaxValue)
                version = (uint)headerVersion;
        }

        return FBXDocument.Create(new FBXHeader(false, version), roots);
    }

    private static uint ReadVersion(ReadOnlySpan<byte> bytes)
    {
        int header = bytes.IndexOf("FBX "u8);
        if (header < 0)
            return 0;

        int offset = header + 4;
        if (!Utf8Parser.TryParse(bytes[offset..], out uint major, out int consumed))
            return 0;
        offset += consumed;

        uint minor = 0;
        uint patch = 0;
        if (offset < bytes.Length && bytes[offset] == (byte)'.')
        {
            offset++;
            if (Utf8Parser.TryParse(bytes[offset..], out uint parsedMinor, out consumed))
            {
                minor = parsedMinor;
                offset += consumed;
            }
            if (offset < bytes.Length && bytes[offset] == (byte)'.')
            {
                offset++;
                if (Utf8Parser.TryParse(bytes[offset..], out uint parsedPatch, out _))
                    patch = parsedPatch;
            }
        }

        try
        {
            return checked(major * 1000 + minor * 100 + patch * 10);
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    private ref struct Parser
    {
        private Lexer _lexer;
        private readonly FBXParserOptions _options;
        private int _nodeCount;
        private long _totalArrayBytes;

        public Parser(ReadOnlySpan<byte> bytes, FBXParserOptions options)
        {
            _lexer = new Lexer(bytes, options.MaxStringBytes);
            _options = options;
            _nodeCount = 0;
            _totalArrayBytes = 0;
        }

        public List<FBXNode> ReadDocument() => ReadBlock(0, expectClose: false);

        private List<FBXNode> ReadBlock(int depth, bool expectClose)
        {
            if (depth > _options.MaxDepth)
                throw new FBXFormatException("Maximum FBX node nesting depth exceeded.");

            var nodes = new List<FBXNode>();
            while (true)
            {
                Token token = _lexer.Peek();
                if (token.Kind == TokenKind.End)
                {
                    if (expectClose)
                        throw new FBXFormatException("Unexpected end of ASCII FBX node block.", token.Offset);
                    return nodes;
                }
                if (token.Kind == TokenKind.CloseBrace)
                {
                    if (!expectClose)
                        throw new FBXFormatException("Unexpected closing brace in ASCII FBX document.", token.Offset);
                    _lexer.Read();
                    return nodes;
                }
                if (token.Kind is TokenKind.NewLine or TokenKind.Comma)
                {
                    _lexer.Read();
                    continue;
                }

                if (++_nodeCount > _options.MaxNodes)
                    throw new FBXFormatException("Maximum FBX node count exceeded.", token.Offset);

                Token nameToken = _lexer.Read();
                if (nameToken.Kind is not (TokenKind.Identifier or TokenKind.String))
                    throw new FBXFormatException("Expected an FBX node name.", nameToken.Offset);

                Token colon = _lexer.Read();
                if (colon.Kind != TokenKind.Colon)
                    throw new FBXFormatException("Expected ':' after an FBX node name.", colon.Offset);

                var node = new FBXNode(_lexer.GetText(nameToken));
                ReadProperties(node);

                // FBX exporters may place the opening brace on the following line.
                while (_lexer.Peek().Kind == TokenKind.NewLine)
                    _lexer.Read();

                if (_lexer.Peek().Kind == TokenKind.OpenBrace)
                {
                    _lexer.Read();
                    foreach (FBXNode child in ReadBlock(depth + 1, expectClose: true))
                        node.AddChild(child);
                }
                nodes.Add(node);
            }
        }

        private void ReadProperties(FBXNode node)
        {
            while (true)
            {
                Token token = _lexer.Peek();
                if (token.Kind is TokenKind.End or TokenKind.NewLine or TokenKind.OpenBrace or TokenKind.CloseBrace)
                    return;
                if (token.Kind == TokenKind.Comma)
                {
                    _lexer.Read();
                    continue;
                }
                if (token.Kind == TokenKind.Star)
                {
                    _lexer.Read();
                    node.AddProperty(ReadScalar());
                    continue;
                }
                if (node.Name == "a")
                {
                    ReadArrayProperties(node);
                    return;
                }
                if (node.Properties.Count >= _options.MaxPropertiesPerNode)
                    throw new FBXFormatException("ASCII FBX node property count exceeds the configured limit.", token.Offset);
                node.AddProperty(ReadScalar());
            }
        }

        private void ReadArrayProperties(FBXNode node)
        {
            var numbers = new List<double>();
            var other = new List<object?>();
            bool numeric = true;

            while (true)
            {
                Token token = _lexer.Peek();
                if (token.Kind is TokenKind.End or TokenKind.OpenBrace or TokenKind.CloseBrace)
                    break;
                if (token.Kind == TokenKind.NewLine)
                {
                    _lexer.Read();
                    continue;
                }
                if (token.Kind == TokenKind.Comma)
                {
                    _lexer.Read();
                    continue;
                }

                object? value = ReadScalar();
                if (numeric && value is long integer)
                    numbers.Add(integer);
                else if (numeric && value is double number)
                    numbers.Add(number);
                else
                {
                    if (numeric)
                    {
                        foreach (double existingNumber in numbers)
                            other.Add(existingNumber);
                        numbers.Clear();
                        numeric = false;
                    }
                    other.Add(value);
                }

                int elementCount = numbers.Count + other.Count;
                if (elementCount > _options.MaxArrayElements ||
                    elementCount > _options.MaxArrayBytes / sizeof(double))
                    throw new FBXFormatException("ASCII FBX array exceeds the configured element or decoded-byte limit.", token.Offset);
            }

            int finalCount = numbers.Count + other.Count;
            long estimatedBytes = numeric
                ? (long)finalCount * sizeof(double)
                : (long)finalCount * IntPtr.Size + other.OfType<string>().Sum(static value => (long)value.Length * sizeof(char));
            if (estimatedBytes > _options.MaxArrayBytes ||
                estimatedBytes > _options.MaxTotalArrayBytes - _totalArrayBytes)
                throw new FBXFormatException("ASCII FBX arrays exceed the configured decoded-byte budget.");

            _totalArrayBytes += estimatedBytes;
            node.AddProperty(numeric ? numbers.ToArray() : other.ToArray());
        }

        private object? ReadScalar()
        {
            Token token = _lexer.Read();
            if (token.Kind == TokenKind.String)
                return _lexer.GetText(token);
            if (token.Kind != TokenKind.Identifier)
                throw new FBXFormatException("Expected an FBX scalar property.", token.Offset);

            ReadOnlySpan<byte> value = _lexer.GetBytes(token);
            if (Utf8Parser.TryParse(value, out long integer, out int consumed) && consumed == value.Length)
                return integer;
            if (Utf8Parser.TryParse(value, out double number, out consumed) && consumed == value.Length)
                return number;
            if (value.SequenceEqual("true"u8))
                return true;
            if (value.SequenceEqual("false"u8))
                return false;
            return _lexer.GetText(token);
        }
    }

    private enum TokenKind : byte
    {
        End,
        Identifier,
        String,
        NewLine,
        Colon,
        Comma,
        OpenBrace,
        CloseBrace,
        Star
    }

    private readonly record struct Token(TokenKind Kind, int Start, int Length, int Offset);

    private ref struct Lexer
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly int _maxStringBytes;
        private int _position;
        private Token _lookahead;
        private bool _hasLookahead;

        public Lexer(ReadOnlySpan<byte> bytes, int maxStringBytes)
        {
            _bytes = bytes;
            _maxStringBytes = maxStringBytes;
            _position = 0;
            _lookahead = default;
            _hasLookahead = false;
        }

        public Token Peek()
        {
            if (!_hasLookahead)
            {
                _lookahead = ReadCore();
                _hasLookahead = true;
            }
            return _lookahead;
        }

        public Token Read()
        {
            if (_hasLookahead)
            {
                _hasLookahead = false;
                return _lookahead;
            }
            return ReadCore();
        }

        public ReadOnlySpan<byte> GetBytes(Token token) =>
            _bytes.Slice(token.Start, token.Length);

        public string GetText(Token token)
        {
            ReadOnlySpan<byte> value = GetBytes(token);
            if (token.Kind != TokenKind.String || value.IndexOf((byte)'\\') < 0)
                return Encoding.UTF8.GetString(value);

            var builder = new StringBuilder(value.Length);
            int segmentStart = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != (byte)'\\')
                    continue;

                builder.Append(Encoding.UTF8.GetString(value[segmentStart..i]));
                if (i + 1 >= value.Length)
                    throw new FBXFormatException("Unterminated escape sequence in ASCII FBX string.", token.Offset);

                byte escaped = value[++i];
                builder.Append(escaped switch
                {
                    (byte)'n' => '\n',
                    (byte)'r' => '\r',
                    (byte)'t' => '\t',
                    (byte)'"' => '"',
                    (byte)'\\' => '\\',
                    _ => (char)escaped
                });
                segmentStart = i + 1;
            }

            builder.Append(Encoding.UTF8.GetString(value[segmentStart..]));
            return builder.ToString();
        }

        private Token ReadCore()
        {
            while (_position < _bytes.Length)
            {
                byte current = _bytes[_position];
                if (current is (byte)'\n' or (byte)'\r')
                {
                    int start = _position++;
                    if (current == (byte)'\r' && _position < _bytes.Length && _bytes[_position] == (byte)'\n')
                        _position++;
                    return new Token(TokenKind.NewLine, start, _position - start, start);
                }
                if (current == (byte)';')
                {
                    while (_position < _bytes.Length && _bytes[_position] != (byte)'\n')
                        _position++;
                    continue;
                }
                if (current is (byte)' ' or (byte)'\t' or (byte)'\r')
                {
                    _position++;
                    continue;
                }
                break;
            }

            if (_position >= _bytes.Length)
                return new Token(TokenKind.End, _position, 0, _position);

            int offset = _position;
            byte ch = _bytes[_position++];
            switch (ch)
            {
                case (byte)':': return new Token(TokenKind.Colon, offset, 1, offset);
                case (byte)',': return new Token(TokenKind.Comma, offset, 1, offset);
                case (byte)'{': return new Token(TokenKind.OpenBrace, offset, 1, offset);
                case (byte)'}': return new Token(TokenKind.CloseBrace, offset, 1, offset);
                case (byte)'*': return new Token(TokenKind.Star, offset, 1, offset);
                case (byte)'"': return ReadQuoted(offset);
                default:
                {
                    int start = offset;
                    while (_position < _bytes.Length)
                    {
                        byte value = _bytes[_position];
                        if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or
                            (byte)':' or (byte)',' or (byte)'{' or (byte)'}' or (byte)'*' or (byte)';')
                            break;
                        _position++;
                    }
                    return new Token(TokenKind.Identifier, start, _position - start, offset);
                }
            }
        }

        private Token ReadQuoted(int offset)
        {
            int start = _position;
            while (_position < _bytes.Length)
            {
                byte value = _bytes[_position++];
                if (value == (byte)'"')
                {
                    int length = _position - start - 1;
                    if (length > _maxStringBytes)
                        throw new FBXFormatException("ASCII FBX string exceeds the configured limit.", offset);
                    return new Token(TokenKind.String, start, length, offset);
                }

                if (value == (byte)'\\')
                {
                    if (_position >= _bytes.Length)
                        throw new FBXFormatException("Unterminated escape sequence in ASCII FBX string.", offset);
                    _position++;
                }
            }

            throw new FBXFormatException("Unterminated quoted string in ASCII FBX input.", offset);
        }
    }
}
