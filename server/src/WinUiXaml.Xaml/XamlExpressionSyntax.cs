using System;
using System.Text;
using System.Text.RegularExpressions;

namespace WinUiXaml.Xaml
{
    /// <summary>
    /// C# expressions in attribute values, in the syntax of .NET MAUI that the LibreWPF XAML source generator follows:
    /// a braced value that is not a markup extension (<c>{Items.Count}</c>, <c>{= Foo}</c>, <c>{this.Foo}</c>,
    /// <c>{$'...'}</c>, <c>{(s, e) =&gt; ...}</c>). Mirrors <c>XamlExpressions.TryGetExpression</c> in openavalon's
    /// <c>LibreWPF.Xaml.SourceGenerator</c> (and the copies in OpenDevelop and WpfDesigner); change them together.
    /// </summary>
    public static class XamlExpressionSyntax
    {
        // The unprefixed markup extensions of the presentation namespace; a prefixed name is always a markup extension.
        private static readonly string[] s_markupExtensions =
        {
            "Binding", "MultiBinding", "PriorityBinding", "StaticResource", "DynamicResource", "TemplateBinding",
            "RelativeSource", "ComponentResourceKey", "ThemeDictionary",
        };

        private static readonly Regex s_leadingName = new(@"^(?<prefix>[A-Za-z_]\w*:)?(?<name>[A-Za-z_][\w.]*)", RegexOptions.Compiled);

        /// <summary>True when the attribute text (the raw text between the quotes) is an expression.</summary>
        public static bool IsExpression(string? text) => TryLocate(text, out _, out _);

        /// <summary>
        /// The code of an expression: where it starts in <paramref name="text"/> and how long it is, after the opening
        /// brace and an optional <c>=</c>, up to before the closing brace.
        /// </summary>
        public static bool TryLocate(string? text, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (text is null)
            {
                return false;
            }

            int first = 0;
            while (first < text.Length && char.IsWhiteSpace(text[first]))
            {
                first++;
            }

            int last = text.Length - 1;
            while (last >= 0 && char.IsWhiteSpace(text[last]))
            {
                last--;
            }

            if (last - first + 1 < 3 || text[first] != '{' || text[last] != '}' || text[first + 1] == '}')
            {
                return false;
            }

            int codeStart = first + 1;
            while (codeStart < last && char.IsWhiteSpace(text[codeStart]))
            {
                codeStart++;
            }

            if (codeStart >= last)
            {
                return false;
            }

            if (text[codeStart] == '=')
            {
                codeStart++;
                while (codeStart < last && char.IsWhiteSpace(text[codeStart]))
                {
                    codeStart++;
                }

                if (codeStart >= last)
                {
                    return false;
                }
            }
            else
            {
                var match = s_leadingName.Match(text.Substring(codeStart, last - codeStart));
                if (match.Success)
                {
                    if (match.Groups["prefix"].Success)
                    {
                        return false;
                    }

                    var name = match.Groups["name"].Value;
                    if (name.EndsWith("Extension", StringComparison.Ordinal))
                    {
                        name = name.Substring(0, name.Length - "Extension".Length);
                    }

                    if (Array.IndexOf(s_markupExtensions, name) >= 0)
                    {
                        return false;
                    }
                }
            }

            int codeEnd = last;
            while (codeEnd > codeStart && char.IsWhiteSpace(text[codeEnd - 1]))
            {
                codeEnd--;
            }

            start = codeStart;
            length = codeEnd - codeStart;
            return length > 0;
        }

        /// <summary>
        /// The text with XML character references of the five predefined entities decoded, and for each decoded
        /// character the offset it came from in the raw text (one more entry for the end), so a position found in the
        /// decoded code maps back into the document.
        /// </summary>
        public static string Decode(string raw, int start, int length, out int[] rawOffsets)
        {
            var decoded = new StringBuilder(length);
            var map = new int[length + 1];
            int count = 0;
            int i = start;
            int end = start + length;
            while (i < end)
            {
                char c = raw[i];
                int consumed = 1;
                char value = c;
                if (c == '&')
                {
                    foreach (var (entity, replacement) in s_entities)
                    {
                        if (i + entity.Length <= end && string.CompareOrdinal(raw, i, entity, 0, entity.Length) == 0)
                        {
                            consumed = entity.Length;
                            value = replacement;
                            break;
                        }
                    }
                }

                decoded.Append(value);
                map[count++] = i;
                i += consumed;
            }

            map[count] = i;
            rawOffsets = new int[count + 1];
            Array.Copy(map, rawOffsets, count + 1);
            return decoded.ToString();
        }

        private static readonly (string Entity, char Replacement)[] s_entities =
        {
            ("&lt;", '<'), ("&gt;", '>'), ("&amp;", '&'), ("&quot;", '"'), ("&apos;", '\''),
        };
    }
}
