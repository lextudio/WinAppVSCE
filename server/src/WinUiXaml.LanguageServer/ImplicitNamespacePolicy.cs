using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WinUiXaml.Workspace;

namespace WinUiXaml.LanguageServer;

/// <summary>
/// Whether a document is read with implicit namespaces (Simpler XAML). That is a feature of the LibreWPF XAML source
/// generator (XSG) only: BAML projects, WPF, WinUI and Uno keep reporting a missing xmlns. A document qualifies when the
/// server is the LibreWPF profile and its project turned the generator on (<c>UseLibreWpfXamlSourceGenerator</c>) without
/// turning the feature off (<c>LibreWpfSimplerXaml</c> is not <c>false</c>), as the SDK defaults the feature to the overall switch.
/// The properties are read from the project file and the <c>Directory.Build.props</c> files above it, the nearest
/// declaration winning; a directory with several project files is ambiguous and reads as off; a property set under a condition or in another import is not seen, which reads as off.
/// </summary>
internal static class ImplicitNamespacePolicy
{
    private static readonly ConcurrentDictionary<(string Key, string Feature), (string Stamp, bool Enabled)> s_cache = new();

    /// <summary>
    /// Whether the document's project compiles C# expressions (<c>{Count + 1}</c>, the syntax of .NET MAUI): the generator is on and
    /// <c>LibreWpfCSharpExpressions</c> is not <c>false</c>. Same rules, file reading and cache as <see cref="IsEnabled"/>.
    /// </summary>
    public static bool ExpressionsEnabled(XamlFrameworkProfile profile, string? documentPath) =>
        FeatureEnabled(profile, documentPath, "LibreWpfCSharpExpressions");

    public static bool IsEnabled(XamlFrameworkProfile profile, string? documentPath) =>
        FeatureEnabled(profile, documentPath, "LibreWpfSimplerXaml");

    private static bool FeatureEnabled(XamlFrameworkProfile profile, string? documentPath, string featureName)
    {
        if (!string.Equals(profile.Id, "librewpf", StringComparison.Ordinal) || string.IsNullOrEmpty(documentPath))
        {
            return false;
        }

        try
        {
            var files = FindPropertyFiles(Path.GetDirectoryName(documentPath));
            if (files.Count == 0)
            {
                return false;
            }

            var key = (files[^1], featureName);
            var stamp = string.Join("|", files.Select(file => file + ":" + File.GetLastWriteTimeUtc(file).Ticks));
            if (s_cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
            {
                return cached.Enabled;
            }

            var enabled = Evaluate(files, featureName);
            s_cache[key] = (stamp, enabled);
            return enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>Directory.Build.props files from the outermost down, then the project file: the order of evaluation.</summary>
    internal static List<string> FindPropertyFiles(string? startDirectory)
    {
        var props = new List<string>();
        string? project = null;
        for (var directory = startDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (project is null)
            {
                var projects = Directory.GetFiles(directory, "*.csproj");
                if (projects.Length > 1)
                {
                    // Which project owns the document is ambiguous: read the document as written rather than guess.
                    return new List<string>();
                }

                if (projects.Length == 1)
                {
                    project = projects[0];
                }
            }

            var buildProps = Path.Combine(directory, "Directory.Build.props");
            if (project is not null && File.Exists(buildProps))
            {
                props.Add(buildProps);
            }
        }

        if (project is null)
        {
            return new List<string>();
        }

        props.Reverse();
        props.Add(project);
        return props;
    }

    /// <summary>A property under any condition (its own, a group's, an enclosing Choose/When/Otherwise) is not evaluated here.</summary>
    private static bool IsConditional(XElement element) =>
        element.AncestorsAndSelf().Any(ancestor => ancestor.Attribute("Condition") is not null
            || ancestor.Name.LocalName is "Choose" or "When" or "Otherwise");

    private static bool Evaluate(IReadOnlyList<string> files, string featureName)
    {
        string? overall = null;
        string? feature = null;
        foreach (var file in files)
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                if (element.HasElements || IsConditional(element))
                {
                    continue;
                }

                if (element.Name.LocalName == "UseLibreWpfXamlSourceGenerator")
                {
                    overall = element.Value.Trim();
                }
                else if (element.Name.LocalName == featureName)
                {
                    feature = element.Value.Trim();
                }
            }
        }

        return string.Equals(overall, "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(feature, "false", StringComparison.OrdinalIgnoreCase);
    }
}
