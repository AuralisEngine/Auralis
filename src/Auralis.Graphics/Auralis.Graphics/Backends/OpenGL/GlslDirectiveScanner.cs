using System.Buffers;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Small GLSL preprocessor scanner for mesh-shader extension directives. It removes comments and
/// evaluates only literal #if 0/#if 1 conditions. Extension directives in any other conditional
/// branch are rejected as ambiguous rather than pretending to implement the complete GLSL preprocessor.
/// </summary>
internal static class GlslDirectiveScanner
{
    private enum BranchState : byte { Active, Inactive, Ambiguous }

    private struct ConditionalFrame
    {
        internal BranchState ParentState;
        internal bool? AnyEarlierBranchMatched;
        internal bool ElseSeen;

        internal ConditionalFrame(BranchState parentState, bool? anyEarlierBranchMatched)
        {
            ParentState = parentState;
            AnyEarlierBranchMatched = anyEarlierBranchMatched;
            ElseSeen = false;
        }
    }

    internal readonly record struct ExtensionResult(bool Found, bool Enabled, bool Ambiguous, string? Behavior);

    private const int InitialConditionalCapacity = 16;
    private const int StackLineBufferCapacity = 512;

    internal static ExtensionResult FindExtension(string source, string extensionName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionName);

        ReadOnlySpan<char> sourceSpan = source.AsSpan();
        Span<ConditionalFrame> stack = stackalloc ConditionalFrame[InitialConditionalCapacity];
        Span<char> stackLineBuffer = stackalloc char[StackLineBufferCapacity];
        ConditionalFrame[]? rentedStack = null;
        char[]? rentedLineBuffer = null;

        BranchState state = BranchState.Active;
        bool found = false;
        bool enabled = false;
        bool ambiguous = false;
        string? behaviorText = null;
        bool blockComment = false;
        int stackCount = 0;

        try
        {
            int lineStart = 0;
            while (lineStart <= sourceSpan.Length)
            {
                int lineEnd = sourceSpan[lineStart..].IndexOf('\n');
                if (lineEnd < 0)
                    lineEnd = sourceSpan.Length;
                else
                    lineEnd += lineStart;

                ReadOnlySpan<char> sourceLine = sourceSpan[lineStart..lineEnd];
                Span<char> normalizedLine;
                if (sourceLine.Length <= stackLineBuffer.Length)
                {
                    normalizedLine = stackLineBuffer[..sourceLine.Length];
                }
                else
                {
                    if (rentedLineBuffer is null || rentedLineBuffer.Length < sourceLine.Length)
                    {
                        if (rentedLineBuffer is not null)
                            ArrayPool<char>.Shared.Return(rentedLineBuffer);
                        rentedLineBuffer = ArrayPool<char>.Shared.Rent(sourceLine.Length);
                    }

                    normalizedLine = rentedLineBuffer.AsSpan(0, sourceLine.Length);
                }

                int normalizedLength = StripComments(sourceLine, normalizedLine, ref blockComment);
                ReadOnlySpan<char> directiveLine = normalizedLine[..normalizedLength].TrimStart();
                if (!directiveLine.IsEmpty && directiveLine[0] == '#')
                {
                    int cursor = 1;
                    SkipWhitespace(directiveLine, ref cursor);
                    ReadOnlySpan<char> directive = ReadToken(directiveLine, ref cursor);

                    if (directive.SequenceEqual("if") ||
                        directive.SequenceEqual("ifdef") ||
                        directive.SequenceEqual("ifndef"))
                    {
                        BranchState parent = state;
                        bool? condition = directive.SequenceEqual("if")
                            ? EvaluateLiteralCondition(directiveLine[cursor..])
                            : null;

                        if (stackCount == stack.Length)
                        {
                            ConditionalFrame[] next = ArrayPool<ConditionalFrame>.Shared.Rent(
                                checked(stack.Length * 2));
                            stack.CopyTo(next);
                            if (rentedStack is not null)
                                ArrayPool<ConditionalFrame>.Shared.Return(rentedStack);
                            rentedStack = next;
                            stack = next;
                        }

                        stack[stackCount++] = new ConditionalFrame(parent, condition);
                        state = Combine(parent, condition);
                    }
                    else if (directive.SequenceEqual("elif") && stackCount > 0)
                    {
                        int frameIndex = stackCount - 1;
                        ConditionalFrame frame = stack[frameIndex];
                        if (frame.ParentState == BranchState.Inactive)
                            state = BranchState.Inactive;
                        else if (frame.ParentState == BranchState.Ambiguous)
                            state = BranchState.Ambiguous;
                        else if (frame.ElseSeen || frame.AnyEarlierBranchMatched == true)
                            state = BranchState.Inactive;
                        else if (frame.AnyEarlierBranchMatched is null)
                            state = BranchState.Ambiguous;
                        else
                        {
                            bool? condition = EvaluateLiteralCondition(directiveLine[cursor..]);
                            frame.AnyEarlierBranchMatched = condition;
                            stack[frameIndex] = frame;
                            state = Combine(frame.ParentState, condition);
                        }
                    }
                    else if (directive.SequenceEqual("else") && stackCount > 0)
                    {
                        int frameIndex = stackCount - 1;
                        ConditionalFrame frame = stack[frameIndex];
                        frame.ElseSeen = true;
                        stack[frameIndex] = frame;
                        state = frame.ParentState switch
                        {
                            BranchState.Inactive => BranchState.Inactive,
                            BranchState.Ambiguous => BranchState.Ambiguous,
                            _ when frame.AnyEarlierBranchMatched == true => BranchState.Inactive,
                            _ when frame.AnyEarlierBranchMatched == false => BranchState.Active,
                            _ => BranchState.Ambiguous
                        };
                    }
                    else if (directive.SequenceEqual("endif") && stackCount > 0)
                    {
                        ConditionalFrame frame = stack[--stackCount];
                        stack[stackCount] = default;
                        state = frame.ParentState;
                    }
                    else if (directive.SequenceEqual("extension") &&
                             TryParseExtension(directiveLine[cursor..], extensionName, out bool isEnabled, out string? behavior))
                    {
                        if (state == BranchState.Ambiguous)
                            ambiguous = true;
                        else if (state == BranchState.Active)
                        {
                            if (found && !string.Equals(behaviorText, behavior, StringComparison.Ordinal))
                                ambiguous = true;
                            found = true;
                            enabled = isEnabled;
                            behaviorText = behavior;
                        }
                    }
                }

                if (lineEnd == sourceSpan.Length)
                    break;
                lineStart = lineEnd + 1;
            }

            return new ExtensionResult(found, enabled, ambiguous, behaviorText);
        }
        finally
        {
            if (rentedLineBuffer is not null)
                ArrayPool<char>.Shared.Return(rentedLineBuffer);
            if (rentedStack is not null)
                ArrayPool<ConditionalFrame>.Shared.Return(rentedStack);
        }
    }

    private static BranchState Combine(BranchState parent, bool? condition) =>
        parent switch
        {
            BranchState.Inactive => BranchState.Inactive,
            BranchState.Ambiguous => BranchState.Ambiguous,
            _ when condition == true => BranchState.Active,
            _ when condition == false => BranchState.Inactive,
            _ => BranchState.Ambiguous
        };

    private static bool? EvaluateLiteralCondition(ReadOnlySpan<char> expression)
    {
        expression = expression.Trim();
        while (expression.Length >= 2 && expression[0] == '(' && expression[^1] == ')')
            expression = expression[1..^1].Trim();

        if (expression.SequenceEqual("0") || expression.SequenceEqual("0u") ||
            expression.SequenceEqual("0U") || expression.SequenceEqual("false"))
            return false;
        if (expression.SequenceEqual("1") || expression.SequenceEqual("1u") ||
            expression.SequenceEqual("1U") || expression.SequenceEqual("true"))
            return true;
        if (!expression.IsEmpty && expression[0] == '!')
        {
            bool? nested = EvaluateLiteralCondition(expression[1..]);
            return nested is null ? null : !nested.Value;
        }
        return null;
    }

    private static bool TryParseExtension(
        ReadOnlySpan<char> text,
        string expectedName,
        out bool enabled,
        out string? behavior)
    {
        enabled = false;
        behavior = null;
        int cursor = 0;
        SkipWhitespace(text, ref cursor);
        ReadOnlySpan<char> name = ReadToken(text, ref cursor);
        if (!name.SequenceEqual(expectedName.AsSpan()))
            return false;

        SkipWhitespace(text, ref cursor);
        if (cursor >= text.Length || text[cursor++] != ':')
            return false;
        SkipWhitespace(text, ref cursor);
        ReadOnlySpan<char> behaviorToken = ReadToken(text, ref cursor);
        SkipWhitespace(text, ref cursor);
        if (behaviorToken.IsEmpty || cursor != text.Length)
            return false;

        if (behaviorToken.SequenceEqual("require"))
        {
            enabled = true;
            behavior = "require";
            return true;
        }
        if (behaviorToken.SequenceEqual("enable"))
        {
            enabled = true;
            behavior = "enable";
            return true;
        }
        if (behaviorToken.SequenceEqual("warn"))
        {
            enabled = true;
            behavior = "warn";
            return true;
        }
        if (behaviorToken.SequenceEqual("disable"))
        {
            behavior = "disable";
            return true;
        }

        return false;
    }

    private static ReadOnlySpan<char> ReadToken(ReadOnlySpan<char> text, ref int cursor)
    {
        int start = cursor;
        while (cursor < text.Length &&
               !char.IsWhiteSpace(text[cursor]) &&
               text[cursor] != ':' &&
               text[cursor] != '(' &&
               text[cursor] != ')')
            cursor++;
        return text[start..cursor];
    }

    private static void SkipWhitespace(ReadOnlySpan<char> text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;
    }

    private static int StripComments(
        ReadOnlySpan<char> source,
        Span<char> destination,
        ref bool blockComment)
    {
        int written = 0;
        bool lineComment = false;
        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i];
            if (lineComment)
            {
                if (current == '\r')
                {
                    destination[written++] = current;
                    lineComment = false;
                }
                else
                    destination[written++] = ' ';
                continue;
            }

            if (blockComment)
            {
                if (current == '*' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    destination[written++] = ' ';
                    destination[written++] = ' ';
                    i++;
                    blockComment = false;
                }
                else
                    destination[written++] = current is '\r' or '\n' ? current : ' ';
                continue;
            }

            if (current == '/' && i + 1 < source.Length)
            {
                char next = source[i + 1];
                if (next == '/' || next == '*')
                {
                    destination[written++] = ' ';
                    destination[written++] = ' ';
                    i++;
                    if (next == '/')
                        lineComment = true;
                    else
                        blockComment = true;
                    continue;
                }
            }

            destination[written++] = current;
        }

        return written;
    }
}
