using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace WinUiXaml.Workspace;

/// <summary>
/// Names the XAML runtime a server process is permitted to inspect. A profile deliberately owns
/// project eligibility and the metadata sentinel: sharing one server binary must never make an
/// Uno document resolve controls from the Windows App SDK, or vice versa.
/// </summary>
public sealed class XamlFrameworkProfile
{
    private XamlFrameworkProfile(string id, string requiredMessage)
    {
        Id = id;
        RequiredMessage = requiredMessage;
    }

    public string Id { get; }

    public string RequiredMessage { get; }

    public string SentinelMetadataName => Id is "wpf" or "librewpf"
        ? "System.Windows.Controls.Button"
        : "Microsoft.UI.Xaml.Controls.Button";

    public bool AllowsMinimalMetadataFallback => Id is "winui" or "wpf" or "librewpf" or "progpu";

    public static XamlFrameworkProfile WinUi { get; } = new(
        "winui",
        "Not supported for this project. Only WinUI 3 projects are supported at this time.");

    public static XamlFrameworkProfile Uno { get; } = new(
        "uno",
        "Not supported for this project. Only Uno Platform WinUI projects are supported at this time.");

    public static XamlFrameworkProfile Wpf { get; } = new(
        "wpf",
        "Not supported for this project. Only WPF projects are supported at this time.");

    public static XamlFrameworkProfile LibreWpf { get; } = new(
        "librewpf",
        "Not supported for this project. Only LibreWPF projects are supported at this time.");

    public static XamlFrameworkProfile ProGpu { get; } = new(
        "progpu",
        "Not supported for this project. Only ProGPU WinUI projects are supported at this time.");

    public static XamlFrameworkProfile Parse(string? id) =>
        id?.ToLowerInvariant() switch
        {
            "winui" or null or "" => WinUi,
            "uno" => Uno,
            "wpf" => Wpf,
            "librewpf" => LibreWpf,
            "progpu" => ProGpu,
            _ => throw new ArgumentException($"Unknown XAML framework profile '{id}'.", nameof(id)),
        };

    internal bool HasSupportedSdk(XElement? project)
    {
        if (project?.Name.LocalName != "Project")
        {
            return false;
        }

        var sdks = new[] { (string?)project?.Attribute("Sdk") }
            .Concat(project?.Elements().Select(element => element.Name.LocalName == "Sdk"
                ? (string?)element.Attribute("Name") : (string?)element.Attribute("Sdk")) ?? Enumerable.Empty<string?>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim().Split('/')[0])
            .ToArray();

        if (sdks.Length == 0)
        {
            return false;
        }

        return Id switch
        {
            "uno" or "progpu" => sdks.All(sdk => sdk.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) ||
                                                   sdk.Equals("Uno.Sdk", StringComparison.OrdinalIgnoreCase)),
            "librewpf" => sdks.All(sdk => sdk.Equals("LibreWPF.Sdk", StringComparison.OrdinalIgnoreCase)),
            _ => sdks.All(sdk => sdk.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) ||
                                 sdk.Equals("Microsoft.NET.Sdk.WindowsDesktop", StringComparison.OrdinalIgnoreCase)),
        };
    }

    internal bool IsEligible(MsBuildCli.Evaluation evaluation) => Id switch
    {
        // The no-package shape is a deliberate design-host fixture. It is the only case in
        // which WinUI may use the small built-in metadata fallback; a project with any package
        // references must supply a real Windows App SDK closure instead.
        "winui" => evaluation.IsWinUiProject ||
                   (evaluation.UseWinUi && evaluation.PackageReferences.IsDefaultOrEmpty),
        "wpf" => evaluation.UseWpf,
        "librewpf" => evaluation.UseWpf,
        "progpu" => evaluation.IsProGpuWinUiProject,
        "uno" => !evaluation.IsProGpuWinUiProject && evaluation.PackageReferences.Any(
            package => string.Equals(package, "Uno.WinUI", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
