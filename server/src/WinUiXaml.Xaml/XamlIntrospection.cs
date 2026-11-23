namespace WinUiXaml.Xaml
{
    /// <summary>Helpers that read well-known XAML directives (like x:Class) out of a parsed document.</summary>
    public static class XamlIntrospection
    {
        /// <summary>The XAML language namespace that the <c>x:</c> prefix conventionally binds to.</summary>
        public const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>Attempts to read the root element's x:Class directive, identifying it by the namespace the prefix resolves to (not by the literal prefix text)</summary>
        public static bool TryGetClass(XamlDocument document, out string className, out TextSpan valueSpan)
        {
            className = string.Empty;
            valueSpan = default;

            var root = document.Root;
            if (root == null)
            {
                return false;
            }

            for (int i = 0; i < root.Attributes.Count; i++)
            {
                var attr = root.Attributes[i];
                if (!attr.Name.HasPrefix || attr.Name.LocalName != "Class")
                {
                    continue;
                }

                if (!root.NamespaceScope.TryResolvePrefix(attr.Name.Prefix, out var uri) || uri != XamlNamespace)
                {
                    continue;
                }

                if (attr.Value != null && attr.Value.Text.Length > 0)
                {
                    className = attr.Value.Text;
                    valueSpan = attr.Value.InnerSpan;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Parses <paramref name="text"/> and returns its <c>x:Class</c> value, or null. A document that declares no
        /// namespaces (Simpler XAML of the LibreWPF XAML source generator) is read once more with implicit namespaces, so its
        /// <c>x:Class</c> is found too; only the class name is taken from that second reading, never a diagnostic.
        /// </summary>
        public static string? GetClass(string text)
        {
            var document = XamlParser.Parse(text);
            if (TryGetClass(document, out var name, out _))
            {
                return name;
            }

            // Most documents without an x:Class (resource dictionaries, code-less files) never mention one: do not parse them twice.
            if (text is null || text.IndexOf("Class", System.StringComparison.Ordinal) < 0)
            {
                return null;
            }

            return TryGetClass(XamlParser.Parse(text, implicitNamespaces: true), out var implicitName, out _) ? implicitName : null;
        }
    }
}
