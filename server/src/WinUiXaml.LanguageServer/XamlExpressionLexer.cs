using System;
using System.Collections.Generic;

namespace WinUiXaml.LanguageServer;

/// <summary>
/// A small lexer for the C# expressions of the XAML source generator (the syntax of .NET MAUI): <c>{Count + 1}</c>,
/// <c>{= Name ?? 'none'}</c>, <c>{$'Total: {Price:C2}'}</c>, <c>{Count GT 0 AND IsOn}</c>, <c>{(s, e) =&gt; Save()}</c>.
/// It knows the MAUI differences from C# (single-quoted strings, the operator words) and classifies words by
/// position only, because deciding whether a name is a binding, a page member or a type needs the compilation.
/// </summary>
internal static class XamlExpressionLexer
{
    internal enum Kind { Variable, Property, Method, Keyword, String, Number, Operator }

    internal readonly record struct Token(int Start, int Length, Kind Kind);

    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "this", "new", "true", "false", "null", "is", "as", "typeof", "nameof", "default", "async", "await", "not", "and", "or", "in",
    };

    /// <summary>Tokens of <paramref name="code"/> (already decoded of XML entities); offsets are into <paramref name="code"/>.</summary>
    public static List<Token> Lex(string code)
    {
        var tokens = new List<Token>();
        LexRange(code, 0, code.Length, tokens);
        return tokens;
    }

    private static void LexRange(string code, int start, int end, List<Token> tokens)
    {
        int i = start;
        char previousSignificant = '\0';
        while (i < end)
        {
            char c = code[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '$' && i + 1 < end && code[i + 1] == '\'')
            {
                i = LexInterpolated(code, i, end, tokens);
                previousSignificant = '\'';
                continue;
            }

            if (c == '\'' || c == '"')
            {
                int close = SkipString(code, i, end, c);
                tokens.Add(new Token(i, close - i, Kind.String));
                i = close;
                previousSignificant = c;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < end && char.IsDigit(code[i + 1]) && !IsOperandEnd(previousSignificant)))
            {
                int j = i + 1;
                while (j < end && (char.IsLetterOrDigit(code[j]) || code[j] == '.' || code[j] == '_'))
                {
                    j++;
                }

                tokens.Add(new Token(i, j - i, Kind.Number));
                i = j;
                previousSignificant = '0';
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < end && (char.IsLetterOrDigit(code[j]) || code[j] == '_'))
                {
                    j++;
                }

                var word = code.Substring(i, j - i);
                bool spaced = i > start && char.IsWhiteSpace(code[i - 1]) && j < end && char.IsWhiteSpace(code[j]);
                int k = j;
                while (k < end && char.IsWhiteSpace(code[k]))
                {
                    k++;
                }

                Kind kind;
                if (spaced && IsAliasWord(word))
                {
                    kind = Kind.Operator;
                }
                else if (s_keywords.Contains(word))
                {
                    kind = Kind.Keyword;
                }
                else if (previousSignificant == '.')
                {
                    kind = k < end && code[k] == '(' ? Kind.Method : Kind.Property;
                }
                else
                {
                    kind = k < end && code[k] == '(' ? Kind.Method : Kind.Variable;
                }

                tokens.Add(new Token(i, j - i, kind));
                i = j;
                previousSignificant = 'a';
                continue;
            }

            int length = OperatorLength(code, i, end);
            if (length > 0)
            {
                tokens.Add(new Token(i, length, Kind.Operator));
                i += length;
                previousSignificant = c;
                continue;
            }

            previousSignificant = c;
            i++;
        }
    }

    private static bool IsOperandEnd(char c) => c == 'a' || c == '0' || c == ')' || c == ']' || c == '\'' || c == '"';

    private static bool IsAliasWord(string word) =>
        string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase) || string.Equals(word, "OR", StringComparison.OrdinalIgnoreCase)
        || string.Equals(word, "LT", StringComparison.OrdinalIgnoreCase) || string.Equals(word, "GT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(word, "LTE", StringComparison.OrdinalIgnoreCase) || string.Equals(word, "GTE", StringComparison.OrdinalIgnoreCase);

    // The end offset of a quoted string that starts at <paramref name="open"/>; an unterminated one runs to the end.
    private static int SkipString(string code, int open, int end, char quote)
    {
        int i = open + 1;
        while (i < end)
        {
            if (code[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (code[i] == quote)
            {
                return i + 1;
            }

            i++;
        }

        return end;
    }

    private static int OperatorLength(string code, int i, int end)
    {
        string two = i + 1 < end ? code.Substring(i, 2) : string.Empty;
        if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=" or "??" or "?." or "=>" or "?[")
        {
            return 2;
        }

        return code[i] is '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!' or '?' or ':' or '=' or '&' or '|' or '^' or '~' ? 1 : 0;
    }

    // $'text {hole} text': string tokens for the literal parts (and the quotes), the holes lexed as expressions.
    private static int LexInterpolated(string code, int open, int end, List<Token> tokens)
    {
        int literalStart = open;
        int i = open + 2;
        while (i < end)
        {
            char c = code[i];
            if (c == '\\')
            {
                i += 2;
            }
            else if (c == '{' && i + 1 < end && code[i + 1] == '{')
            {
                i += 2;
            }
            else if (c == '{')
            {
                tokens.Add(new Token(literalStart, i + 1 - literalStart, Kind.String));
                int holeEnd = FindHoleEnd(code, i + 1, end);
                int formatAt = FindFormatSeparator(code, i + 1, holeEnd);
                LexRange(code, i + 1, formatAt, tokens);
                if (formatAt < holeEnd)
                {
                    tokens.Add(new Token(formatAt, holeEnd - formatAt, Kind.String));
                }

                i = holeEnd;
                literalStart = holeEnd;
                if (i < end)
                {
                    i++;
                }
            }
            else if (c == '\'')
            {
                tokens.Add(new Token(literalStart, i + 1 - literalStart, Kind.String));
                return i + 1;
            }
            else
            {
                i++;
            }
        }

        if (literalStart < end)
        {
            tokens.Add(new Token(literalStart, end - literalStart, Kind.String));
        }

        return end;
    }

    // The offset of the closing brace of the hole whose code starts at <paramref name="start"/>.
    private static int FindHoleEnd(string code, int start, int end)
    {
        int depth = 0;
        int i = start;
        while (i < end)
        {
            char c = code[i];
            if (c == '\'' || c == '"')
            {
                i = SkipString(code, i, end, c);
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']')
            {
                depth--;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }

            i++;
        }

        return end;
    }

    // The format specifier (":F2") starts at the first top-level ':' that is not part of a conditional.
    private static int FindFormatSeparator(string code, int start, int end)
    {
        int depth = 0;
        bool conditional = false;
        int i = start;
        while (i < end)
        {
            char c = code[i];
            if (c == '\'' || c == '"')
            {
                i = SkipString(code, i, end, c);
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                depth--;
            }
            else if (depth == 0 && c == '?' && i + 1 < end && code[i + 1] is not ('?' or '.' or '['))
            {
                conditional = true;
            }
            else if (depth == 0 && c == ':')
            {
                if (conditional)
                {
                    conditional = false;
                }
                else
                {
                    return i;
                }
            }

            i++;
        }

        return end;
    }
}
