using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace WinUiXaml.Workspace;

public static class WinUiProjectEligibility
{
    public const string RequiredMessage =
        "Not supported for this project. Only WinUI 3 projects are supported at this time.";

    internal static bool Evaluate(
        string projectPath, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken)
        => Evaluate(XamlFrameworkProfile.WinUi, projectPath, properties, cancellationToken);

    internal static bool Evaluate(
        XamlFrameworkProfile profile,
        string projectPath,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = XmlReader.Create(projectPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            if (!profile.HasSupportedSdk(XDocument.Load(reader).Root))
            {
                return false;
            }

            var evaluation = MsBuildCli.Evaluate(projectPath, properties, cancellationToken);
            if (!properties.ContainsKey("TargetFramework") &&
                MsBuildCli.SelectTargetFramework(evaluation.TargetFrameworks) is { } framework)
            {
                var pinned = properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                pinned["TargetFramework"] = framework;
                evaluation = MsBuildCli.Evaluate(projectPath, pinned, cancellationToken);
            }
            return profile.IsEligible(evaluation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException ||
            ex is MsBuildUnavailableException { HostMissing: false })
        {
            Console.Error.WriteLine($"[winui-xaml-ls] could not confirm WinUI project '{projectPath}': {ex.Message}");
            return false;
        }
    }

    internal static bool HasSupportedSdk(XElement? project)
        => XamlFrameworkProfile.WinUi.HasSupportedSdk(project);
}
