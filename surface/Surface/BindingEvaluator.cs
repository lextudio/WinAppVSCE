using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Surface;

/// <summary>
/// Design-time <c>{x:Bind}</c> evaluation for the markup preview. Each binding is evaluated against one
/// design-time page instance and its value is substituted into the markup before the render, so an edited
/// binding shows a real value without a project build.
/// <para>
/// Three expression forms are supported: a plain member path (reflection), a function/static call (compiled with
/// Roslyn against the loaded project assembly), and either of those followed by <c>Converter</c>,
/// <c>ConverterParameter</c>/<c>ConverterLanguage</c>, <c>FallbackValue</c> or <c>TargetNullValue</c>.
/// <c>{x:Bind}</c> has no <c>StringFormat</c> (the XAML compiler rejects it), so a format comes from a converter
/// or a function. Everything else - attached-property targets, templates, two-way semantics - is left for
/// <see cref="XamlCleaner"/>, which keeps the existing placeholder behaviour. A binding that cannot be evaluated
/// is skipped and logged, never thrown into the render.
/// </para>
/// </summary>
internal static class BindingEvaluator
{
    /// <summary>
    /// Attributes the evaluator may substitute. Text targets take any value as-is. The typed names are bool, number
    /// and enum properties whose XAML parser conversion accepts the text <see cref="Format"/> produces ("True",
    /// "120.5", "Visible"); they are listed by name because the target type is not resolved from the element type
    /// here - an unknown property name is simply not in this set, and a value that would not parse is left to the
    /// cleaner rather than guessed at.
    /// </summary>
    private static readonly HashSet<string> EvaluableAttributes = new(StringComparer.Ordinal)
    {
        // Text.
        "Text", "Content", "Header", "PlaceholderText", "Title", "Description", "Label", "Subtitle",
        "ToolTipService.ToolTip",
        // Boolean.
        "IsEnabled", "IsReadOnly", "IsChecked", "IsHitTestVisible", "IsTabStop", "IsExpanded", "IsSelected",
        "IsOn", "IsDropDownOpen", "IsOpen", "IsIndeterminate", "AcceptsReturn",
        // Number.
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Opacity", "FontSize", "Value",
        "Minimum", "Maximum", "SelectedIndex", "TabIndex", "CharacterSpacing", "LineHeight", "StrokeThickness",
        "Canvas.Left", "Canvas.Top", "Canvas.ZIndex", "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan",
        // Enum.
        "Visibility", "HorizontalAlignment", "VerticalAlignment", "TextAlignment", "FlowDirection", "Orientation",
        "TextWrapping", "TextTrimming", "TextDecorations"
    };

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Rewritten expression -> its compiled invoker (null when it did not compile; failures log once).</summary>
    private static readonly ConcurrentDictionary<string, Lazy<MethodInfo?>> CompiledExpressions = new(StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    /// <param name="xaml">The markup about to be rendered (never modified in place).</param>
    /// <param name="instance">The design-time page instance whose members the bindings read.</param>
    /// <param name="userAssembly">The project assembly the page type came from.</param>
    /// <param name="userDllPath">The <c>--user-dll</c> path, used as the Roslyn metadata reference (the assembly is loaded from bytes).</param>
    /// <param name="log">The host log sink.</param>
    internal static string Apply(string xaml, object instance, Assembly? userAssembly, string? userDllPath, Action<string> log)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xaml);
        }
        catch (Exception ex)
        {
            log($"LIVEBIND: markup is not parseable ({ex.GetType().Name}: {ex.Message}); no binding evaluation");
            return xaml;
        }

        var converterTypes = ReadResources(document);
        var namespaces = ReadNamespacePrefixes(document);
        int evaluated = 0, skipped = 0;
        foreach (var element in document.Descendants())
        {
            if (element.Ancestors().Any(a => a.Name.LocalName is "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate"))
                continue;
            foreach (var attribute in element.Attributes().ToList())
            {
                var value = attribute.Value;
                if (!value.StartsWith("{x:Bind", StringComparison.Ordinal) || !value.EndsWith('}'))
                    continue;
                if (!EvaluableAttributes.Contains(attribute.Name.LocalName))
                {
                    skipped++;
                    continue;
                }
                if (!TryExpression(value, out var expression, out var options))
                {
                    skipped++;
                    continue;
                }

                if (!TryEvaluate(instance, userAssembly, expression, namespaces, userDllPath, log, out var result, out var error))
                {
                    skipped++;
                    log($"LIVEBIND: skipped '{expression}': {error}");
                    continue;
                }

                if (!TryConvert(result, options, instance, userAssembly, converterTypes, log, out result, out error))
                {
                    skipped++;
                    log($"LIVEBIND: skipped '{expression}': {error}");
                    continue;
                }

                if (result is null)
                {
                    if (options.TryGetValue("FallbackValue", out var fallback))
                        attribute.Value = Unquote(fallback);
                    else if (options.TryGetValue("TargetNullValue", out var targetNull))
                        attribute.Value = Unquote(targetNull);
                    else
                        attribute.Remove();
                }
                else
                {
                    attribute.Value = Format(result);
                }
                evaluated++;
            }
        }

        log($"LIVEBIND: evaluated {evaluated}, skipped {skipped}");
        return evaluated == 0 ? xaml : document.ToString(SaveOptions.DisableFormatting);
    }

    // ---- expression and option parsing ------------------------------------------------

    private static bool TryExpression(string value, out string expression, out Dictionary<string, string> options)
    {
        expression = "";
        options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inner = value.Substring("{x:Bind".Length).Trim();
        inner = inner[..^1].Trim();   // the trailing }
        var parts = SplitTopLevel(inner);
        expression = parts.Count > 0 ? parts[0] : "";
        for (int i = 1; i < parts.Count; i++)
        {
            var equals = parts[i].IndexOf('=');
            if (equals > 0)
                options[parts[i][..equals].Trim()] = parts[i][(equals + 1)..].Trim();
        }
        return expression.Length > 0;
    }

    /// <summary>Splits markup-extension arguments on commas outside quotes and nested (), {}, [].</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        char quote = '\0';
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }

    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] is '\'' or '"' && trimmed[^1] == trimmed[0])
            return trimmed[1..^1];
        return trimmed;
    }

    // ---- expression evaluation -------------------------------------------------------

    private static bool TryEvaluate(object instance, Assembly? userAssembly, string expression,
        Dictionary<string, string> namespaces, string? userDllPath, Action<string> log, out object? value, out string? error)
    {
        if (TryEvaluatePath(instance, expression, out value, out error))
            return true;
        // A plain path that names no member is a genuine miss; anything shaped like a call or a qualified name may
        // still be a function/static binding the compiler can resolve.
        if (!expression.Contains('(') && !expression.Contains(':'))
            return false;
        return TryEvaluateCompiled(instance, userAssembly, expression, namespaces, userDllPath, log, out value, out error);
    }

    private static bool TryEvaluatePath(object instance, string path, out object? value, out string? error)
    {
        value = instance;
        error = null;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value is null)
            {
                error = "null in path";
                return false;
            }
            var type = value.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var property = type.GetProperty(segment, flags);
            if (property is { CanRead: true })
            {
                try
                {
                    value = property.GetValue(value);
                }
                catch (Exception ex)
                {
                    error = $"{type.Name}.{segment} threw {ex.GetType().Name}";
                    return false;
                }
                continue;
            }
            var field = type.GetField(segment, flags);
            if (field is not null)
            {
                value = field.GetValue(value);
                continue;
            }
            error = $"no member '{segment}' on {type.Name}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Compiles the expression as C# against the loaded project assembly, with the page instance in scope for
    /// unqualified member names (<c>Shout(Title)</c> becomes <c>__page.Shout(__page.Title)</c>) and XAML
    /// namespace prefixes mapped to CLR namespaces (<c>local:Helper.Fmt</c> becomes <c>WinUISample.Helper.Fmt</c>).
    /// </summary>
    private static bool TryEvaluateCompiled(object instance, Assembly? userAssembly, string expression,
        Dictionary<string, string> namespaces, string? userDllPath, Action<string> log, out object? value, out string? error)
    {
        value = null;
        error = null;
        var pageType = instance.GetType();
        var rewritten = Rewrite(expression, pageType, namespaces);
        var key = pageType.FullName + "|" + rewritten;
        var compiled = CompiledExpressions
            .GetOrAdd(key, _ => new Lazy<MethodInfo?>(() => Compile(pageType, expression, rewritten, userAssembly, userDllPath, log)))
            .Value;
        if (compiled is null)
        {
            error = "expression did not compile";
            return false;
        }
        try
        {
            value = compiled.Invoke(null, new object?[] { instance });
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            error = $"expression threw {inner.GetType().Name}: {inner.Message}";
            return false;
        }
    }

    private static MethodInfo? Compile(Type pageType, string expression, string rewritten, Assembly? userAssembly, string? userDllPath, Action<string> log)
    {
        try
        {
            var references = References.Value
                .Concat(UserReference(userAssembly, userDllPath) is { } user ? new[] { user } : Array.Empty<MetadataReference>())
                .GroupBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var source = $@"
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace __SurfaceBindings {{
  internal static class Eval {{
    public static object Run({TypeName(pageType)} __page) => (object)({rewritten});
  }}
}}";
            var tree = CSharpSyntaxTree.ParseText(source);
            var compilation = CSharpCompilation.Create(
                "__SurfaceBindings_" + Math.Abs(rewritten.GetHashCode()).ToString("x"),
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            if (!emitted.Success)
            {
                var first = emitted.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
                log($"LIVEBIND: '{expression}' did not compile ({first.Id}: {first.GetMessage()})");
                return null;
            }
            stream.Position = 0;
            var assembly = Assembly.Load(stream.ToArray());
            var method = assembly.GetType("__SurfaceBindings.Eval")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (method is not null)
                log($"LIVEBIND: compiled '{expression}' as '{rewritten}'");
            return method;
        }
        catch (Exception ex)
        {
            log($"LIVEBIND: '{expression}' failed to compile: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string TypeName(Type type)
        => type.FullName?.Replace('+', '.') ?? type.Name;

    /// <summary>
    /// Prefixes page members with <c>__page.</c>, maps <c>prefix:Type</c> to the prefix's CLR namespace, and leaves
    /// literals, member chains and type names alone. Deliberately conservative: a wrong rewrite fails to compile and
    /// the binding is then skipped, which is the same outcome as not handling it at all.
    /// </summary>
    private static string Rewrite(string expression, Type pageType, Dictionary<string, string> namespaces)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        for (var t = pageType; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                members.Add(p.Name);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                members.Add(f.Name);
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                members.Add(m.Name);
        }

        var result = new StringBuilder(expression.Length + 32);
        int i = 0;
        bool afterDot = false;
        while (i < expression.Length)
        {
            var c = expression[i];
            if (c is '\'' or '"')
            {
                // Copy the literal verbatim, honouring a backslash escape.
                var quote = c;
                result.Append(c);
                i++;
                while (i < expression.Length)
                {
                    var l = expression[i];
                    result.Append(l);
                    i++;
                    if (l == '\\' && i < expression.Length)
                    {
                        result.Append(expression[i]);
                        i++;
                        continue;
                    }
                    if (l == quote)
                        break;
                }
                afterDot = false;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                    i++;
                var identifier = expression[start..i];
                // "prefix:Type" - a XAML namespace prefix, mapped to the CLR namespace the document declared.
                if (i < expression.Length && expression[i] == ':' && !afterDot)
                {
                    if (namespaces.TryGetValue(identifier, out var clrNamespace) && clrNamespace.Length > 0)
                        result.Append(clrNamespace).Append('.');
                    else
                        result.Append(identifier).Append(':');
                    i++;   // skip the colon
                    afterDot = true;
                    continue;
                }
                if (!afterDot && members.Contains(identifier))
                    result.Append("__page.");
                result.Append(identifier);
                afterDot = false;
                continue;
            }
            if (c == '.')
                afterDot = true;
            else if (!char.IsWhiteSpace(c))
                afterDot = c == ')' || c == ']';   // a call/index result is a receiver, not a bare member name
            result.Append(c);
            i++;
        }
        return result.ToString();
    }

    // ---- converters, formats, values --------------------------------------------------

    private static bool TryConvert(object? value, Dictionary<string, string> options, object instance, Assembly? userAssembly,
        Dictionary<string, XElement> converterTypes, Action<string> log, out object? result, out string? error)
    {
        result = value;
        error = null;
        if (options.TryGetValue("Converter", out var converterClause))
        {
            if (!TryResolveConverter(converterClause, instance, userAssembly, converterTypes, log, out var converter, out error))
                return false;
            try
            {
                options.TryGetValue("ConverterParameter", out var parameter);
                options.TryGetValue("ConverterLanguage", out var language);
                result = converter.Convert(
                    result,
                    typeof(string),
                    parameter is null ? null : Unquote(parameter),
                    language is null ? null : Unquote(language));
            }
            catch (Exception ex)
            {
                error = $"converter threw {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A converter named by <c>{StaticResource key}</c>: the element declared for that key in the document's resources
    /// (so a XAML-only converter edit stays live), else one the design-time page instance already holds.
    /// </summary>
    private static bool TryResolveConverter(string clause, object instance, Assembly? userAssembly,
        Dictionary<string, XElement> converterTypes, Action<string> log, out IValueConverter converter, out string? error)
    {
        converter = null!;
        error = null;
        var key = StaticResourceKey(clause);
        if (key is null)
        {
            error = $"unsupported converter clause '{clause}'";
            return false;
        }
        if (converterTypes.TryGetValue(key, out var declaration))
        {
            TryCreateFromMarkup(declaration, instance, userAssembly, log, out converter, out error);
            if (converter is not null)
                return true;
        }
        if (TryFindInstanceResource(instance, key, out converter))
            return true;
        error ??= $"no converter resource '{key}'";
        return false;
    }

    private static string? StaticResourceKey(string clause)
    {
        var text = clause.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}'))
            return null;
        var inner = text[1..^1].Trim();
        var space = inner.IndexOf(' ');
        if (space < 0)
            return null;
        var kind = inner[..space];
        if (kind is not ("StaticResource" or "ThemeResource"))
            return null;
        var argument = inner[(space + 1)..].Trim();
        const string prefix = "ResourceKey=";
        return argument.StartsWith(prefix, StringComparison.Ordinal) ? argument[prefix.Length..].Trim() : argument;
    }

    private static bool TryCreateFromMarkup(XElement declaration, object instance, Assembly? userAssembly,
        Action<string> log, out IValueConverter converter, out string? error)
    {
        converter = null!;
        error = null;
        var localName = declaration.Name.LocalName;
        var colon = localName.IndexOf(':');
        var typeName = colon >= 0 ? localName[(colon + 1)..] : localName;
        var ns = declaration.Name.NamespaceName;
        var clrNamespace = ns.StartsWith("using:", StringComparison.Ordinal) ? ns["using:".Length..]
            : ns.StartsWith("clr-namespace:", StringComparison.Ordinal) ? ns["clr-namespace:".Length..].Split(';')[0]
            : null;
        if (clrNamespace is null)
        {
            error = $"resource '{typeName}' is not a project type";
            return false;
        }
        var fullName = clrNamespace + "." + typeName;
        Type? type = userAssembly?.GetType(fullName, throwOnError: false)
            ?? instance.GetType().Assembly.GetType(fullName, throwOnError: false);
        if (type is null)
        {
            error = $"no type '{fullName}' in the project assembly";
            return false;
        }
        try
        {
            var created = Activator.CreateInstance(type);
            if (created is not IValueConverter valueConverter)
            {
                error = $"'{fullName}' is not an IValueConverter";
                return false;
            }
            converter = valueConverter;
            log($"LIVEBIND: converter '{fullName}' created from markup");
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            error = $"converter '{fullName}' could not be created: {inner.GetType().Name}: {inner.Message}";
            return false;
        }
    }

    private static bool TryFindInstanceResource(object instance, string key, out IValueConverter converter)
    {
        converter = null!;
        try
        {
            if (instance is FrameworkElement element && element.Resources.TryGetValue(key, out var value)
                && value is IValueConverter fromPage)
            {
                converter = fromPage;
                return true;
            }
            var app = Application.Current;
            if (app is not null && app.Resources.TryGetValue(key, out var application)
                && application is IValueConverter fromApp)
            {
                converter = fromApp;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>Key -> declaring element for every resource declared in the document.</summary>
    private static Dictionary<string, XElement> ReadResources(XDocument document)
    {
        var resources = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var resourcesElement in document.Descendants().Where(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
        {
            foreach (var declaration in resourcesElement.Elements())
            {
                var key = (string?)declaration.Attribute(XamlNamespace + "Key");
                if (!string.IsNullOrEmpty(key))
                    resources[key] = declaration;
            }
        }
        return resources;
    }

    /// <summary>xmlns prefix -> CLR namespace for <c>using:</c>/<c>clr-namespace:</c> declarations.</summary>
    private static Dictionary<string, string> ReadNamespacePrefixes(XDocument document)
    {
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in document.Root?.Attributes() ?? Enumerable.Empty<XAttribute>())
        {
            if (attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.Xmlns)
            {
                var value = attribute.Value;
                var clr = value.StartsWith("using:", StringComparison.Ordinal) ? value["using:".Length..]
                    : value.StartsWith("clr-namespace:", StringComparison.Ordinal) ? value["clr-namespace:".Length..].Split(';')[0]
                    : null;
                if (!string.IsNullOrEmpty(clr))
                    prefixes[attribute.Name.LocalName] = clr;
            }
        }
        return prefixes;
    }

    /// <summary>
    /// The text the XAML parser would accept for the target: the XAML parser parses numbers and enums with the
    /// invariant culture, so format them that way rather than with the user's culture (120,5 is not a valid Width).
    /// </summary>
    private static string Format(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "True" : "False",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    // ---- Roslyn references ------------------------------------------------------------

    /// <summary>
    /// The project assembly as a metadata reference. The host loads it from bytes, so <see cref="Assembly.Location"/>
    /// is empty; the <c>--user-dll</c> path the host already holds is the file to compile against. Tests pass the
    /// assembly's own location instead.
    /// </summary>
    private static MetadataReference? UserReference(Assembly? userAssembly, string? userDllPath)
    {
        if (userAssembly is null)
            return null;
        try
        {
            var location = userAssembly.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
                return MetadataReference.CreateFromFile(location);
        }
        catch (NotSupportedException)
        {
        }
        try
        {
            if (!string.IsNullOrEmpty(userDllPath) && File.Exists(userDllPath))
                return MetadataReference.CreateFromFile(userDllPath);
        }
        catch
        {
        }
        return null;
    }

    /// <summary>The assemblies already loaded in this process: the project, WinUI and the BCL are all here.</summary>
    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? location = null;
            try { location = assembly.Location; } catch (NotSupportedException) { }
            if (string.IsNullOrEmpty(location) || !File.Exists(location) || !seen.Add(location))
                continue;
            try { references.Add(MetadataReference.CreateFromFile(location)); } catch { }
        }
        return references;
    }
}
