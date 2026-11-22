namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Small GLSL preprocessor scanner for mesh-shader extension directives. It removes comments and
/// evaluates only literal #if 0/#if 1 conditions. Extension directives in any other conditional
/// branch are rejected as ambiguous rather than pretending to implement the complete GLSL preprocessor.
/// </summary>
internal static class GlslDirectiveScanner
{
    private enum BranchState : byte { Active, Inactive, Ambiguous }

    private sealed class ConditionalFrame
    {
        internal required BranchState ParentState;
        internal bool? AnyEarlierBranchMatched;
        internal bool ElseSeen;
    }

    internal readonly record struct ExtensionResult(bool Found, bool Enabled, bool Ambiguous, string? Behavior);

    internal static ExtensionResult FindExtension(string source, string extensionName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionName);

        string text = StripComments(source);
        BranchState state = BranchState.Active;
        List<ConditionalFrame> stack = new(4);
        bool found = false;
        bool enabled = false;
        bool ambiguous = false;
        string? behaviorText = null;

        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;

            ReadOnlySpan<char> line = text.AsSpan(lineStart, lineEnd - lineStart);
            ReadOnlySpan<char> directiveLine = line.TrimStart();
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
                    stack.Add(new ConditionalFrame { ParentState = parent, AnyEarlierBranchMatched = condition });
                    state = Combine(parent, condition);
                }
                else if (directive.SequenceEqual("elif") && stack.Count > 0)
                {
                    ConditionalFrame frame = stack[^1];
                    if (frame.ParentState == BranchState.Inactive)
                    {
                        state = BranchState.Inactive;
                    }
                    else if (frame.ParentState == BranchState.Ambiguous)
                    {
                        state = BranchState.Ambiguous;
                    }
                    else if (frame.ElseSeen || frame.AnyEarlierBranchMatched == true)
                    {
                        state = BranchState.Inactive;
                    }
                    else if (frame.AnyEarlierBranchMatched is null)
                    {
                        state = BranchState.Ambiguous;
                    }
                    else
                    {
                        bool? condition = EvaluateLiteralCondition(directiveLine[cursor..]);
                        frame.AnyEarlierBranchMatched = condition;
                        state = Combine(frame.ParentState, condition);
                    }
                }
                else if (directive.SequenceEqual("else") && stack.Count > 0)
                {
                    ConditionalFrame frame = stack[^1];
                    frame.ElseSeen = true;
                    state = frame.ParentState switch
                    {
                        BranchState.Inactive => BranchState.Inactive,
                        BranchState.Ambiguous => BranchState.Ambiguous,
                        _ when frame.AnyEarlierBranchMatched == true => BranchState.Inactive,
                        _ when frame.AnyEarlierBranchMatched == false => BranchState.Active,
                        _ => BranchState.Ambiguous
                    };
                }
                else if (directive.SequenceEqual("endif") && stack.Count > 0)
                {
                    ConditionalFrame frame = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    state = frame.ParentState;
                }
                else if (directive.SequenceEqual("extension"))
                {
                    if (TryParseExtension(directiveLine[cursor..], extensionName, out bool isEnabled, out string? behavior))
                    {
                        if (state == BranchState.Ambiguous)
                        {
                            ambiguous = true;
                        }
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
            }

            if (lineEnd == text.Length)
                break;
            lineStart = lineEnd + 1;
        }

        return new ExtensionResult(found, enabled, ambiguous, behaviorText);
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

        if (behaviorToken.SequenceEqual("require") ||
            behaviorToken.SequenceEqual("enable") ||
            behaviorToken.SequenceEqual("warn"))
        {
            enabled = true;
            behavior = behaviorToken.ToString();
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

    private static string StripComments(string source)
    {
        char[] output = source.ToCharArray();
        bool lineComment = false;
        bool blockComment = false;
        for (int i = 0; i < output.Length; i++)
        {
            if (lineComment)
            {
                if (output[i] is '\r' or '\n')
                    lineComment = false;
                else
                    output[i] = ' ';
                continue;
            }

            if (blockComment)
            {
                if (output[i] == '*' && i + 1 < output.Length && output[i + 1] == '/')
                {
                    output[i] = output[i + 1] = ' ';
                    i++;
                    blockComment = false;
                }
                else if (output[i] is not ('\r' or '\n'))
                {
                    output[i] = ' ';
                }
                continue;
            }

            if (output[i] == '/' && i + 1 < output.Length)
            {
                if (output[i + 1] == '/')
                {
                    output[i] = output[i + 1] = ' ';
                    i++;
                    lineComment = true;
                }
                else if (output[i + 1] == '*')
                {
                    output[i] = output[i + 1] = ' ';
                    i++;
                    blockComment = true;
                }
            }
        }
        return new string(output);
    }
}
