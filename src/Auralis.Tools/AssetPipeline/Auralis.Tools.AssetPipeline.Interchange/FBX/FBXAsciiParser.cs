using System.Globalization;
using System.Text;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

internal static class FBXAsciiParser
{
    public static FBXDocument Parse(ReadOnlyMemory<byte> data, FBXParserOptions options)
    {
        string text;
        try
        {
            ReadOnlySpan<byte> bytes = data.Span;
            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                bytes = bytes[3..];
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FBXFormatException("ASCII FBX input is not valid UTF-8.", 0, ex);
        }

        uint version = ReadVersion(text);
        var parser = new Parser(text, options);
        List<FBXNode> roots = parser.ReadDocument();
        return FBXDocument.Create(new FBXHeader(false, version), roots);
    }

    private static uint ReadVersion(string text)
    {
        int header = text.IndexOf("FBX ", StringComparison.Ordinal);
        if (header < 0) return 0;
        int start = header + 4;
        int end = start;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
        string[] parts = text[start..end].Split('.');
        if (parts.Length == 0 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint major))
            return 0;
        uint minor = 0, patch = 0;
        if (parts.Length > 1) uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
        if (parts.Length > 2) uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch);
        return major * 1000 + minor * 100 + patch * 10;
    }

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private readonly FBXParserOptions _options;
        private int _nodeCount;

        public Parser(string text, FBXParserOptions options)
        {
            _lexer = new Lexer(text, options.MaxStringBytes);
            _options = options;
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
                    if (expectClose) throw new FBXFormatException("Unexpected end of ASCII FBX node block.", token.Offset);
                    return nodes;
                }
                if (token.Kind == TokenKind.CloseBrace)
                {
                    if (!expectClose) throw new FBXFormatException("Unexpected closing brace in ASCII FBX document.", token.Offset);
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

                var node = new FBXNode(nameToken.Value);
                ReadProperties(node);

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
                if (token.Kind is TokenKind.End or TokenKind.NewLine or TokenKind.OpenBrace or TokenKind.CloseBrace)
                    break;
                if (token.Kind == TokenKind.Comma)
                {
                    _lexer.Read();
                    continue;
                }

                object? value = ReadScalar();
                if (numeric && value is long integer) numbers.Add(integer);
                else if (numeric && value is double number) numbers.Add(number);
                else
                {
                    if (numeric)
                    {
                        foreach (double number in numbers) other.Add(number);
                        numbers.Clear();
                        numeric = false;
                    }
                    other.Add(value);
                }

                if (numbers.Count + other.Count > _options.MaxArrayElements)
                    throw new FBXFormatException("ASCII FBX array exceeds the configured element limit.", token.Offset);
            }

            node.AddProperty(numeric ? numbers.ToArray() : other.ToArray());
        }

        private object? ReadScalar()
        {
            Token token = _lexer.Read();
            if (token.Kind == TokenKind.String) return token.Value;
            if (token.Kind != TokenKind.Identifier)
                throw new FBXFormatException("Expected an FBX scalar property.", token.Offset);
            if (long.TryParse(token.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                return integer;
            if (double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return number;
            if (string.Equals(token.Value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(token.Value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return token.Value;
        }
    }

    private enum TokenKind : byte { End, Identifier, String, NewLine, Colon, Comma, OpenBrace, CloseBrace, Star }
    private readonly record struct Token(TokenKind Kind, string Value, int Offset);

    private sealed class Lexer
    {
        private readonly string _text;
        private readonly int _maxStringBytes;
        private int _position;
        private Token _lookahead;
        private bool _hasLookahead;

        public Lexer(string text, int maxStringBytes)
        {
            _text = text;
            _maxStringBytes = maxStringBytes;
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

        private Token ReadCore()
        {
            while (_position < _text.Length)
            {
                char ch = _text[_position];
                if (ch == '\n')
                {
                    int start = _position++;
                    return new Token(TokenKind.NewLine, "\n", start);
                }
                if (ch == ';')
                {
                    while (_position < _text.Length && _text[_position] != '\n') _position++;
                    continue;
                }
                if (ch is ' ' or '\t' or '\r')
                {
                    _position++;
                    continue;
                }
                break;
            }

            if (_position >= _text.Length) return new Token(TokenKind.End, string.Empty, _position);
            int offset = _position;
            char current = _text[_position++];
            switch (current)
            {
                case ':': return new Token(TokenKind.Colon, ":", offset);
                case ',': return new Token(TokenKind.Comma, ",", offset);
                case '{': return new Token(TokenKind.OpenBrace, "{", offset);
                case '}': return new Token(TokenKind.CloseBrace, "}", offset);
                case '*': return new Token(TokenKind.Star, "*", offset);
                case '"': return ReadQuoted(offset);
                default:
                    while (_position < _text.Length)
                    {
                        char c = _text[_position];
                        if (char.IsWhiteSpace(c) || c is ':' or ',' or '{' or '}' or '*' or ';') break;
                        _position++;
                    }
                    return new Token(TokenKind.Identifier, _text[offset.._position], offset);
            }
        }

        private Token ReadQuoted(int offset)
        {
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                char ch = _text[_position++];
                if (ch == '"') return new Token(TokenKind.String, builder.ToString(), offset);
                if (ch == '\\')
                {
                    if (_position >= _text.Length)
                        throw new FBXFormatException("Unterminated escape sequence in ASCII FBX string.", offset);
                    char escaped = _text[_position++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => escaped
                    });
                }
                else builder.Append(ch);

                if (builder.Length > _maxStringBytes)
                    throw new FBXFormatException("ASCII FBX string exceeds the configured limit.", offset);
            }
            throw new FBXFormatException("Unterminated quoted string in ASCII FBX input.", offset);
        }
    }
}
