using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WinUiXaml.Workspace;
using Xunit;

namespace WinUiXaml.Workspace.Tests;

public sealed class WinUiProjectEligibilityTests
{
    [Theory]
    [InlineData("true", "Windows", "Microsoft.WindowsAppSDK", false, true)]
    [InlineData("TRUE", "windows", "Microsoft.WindowsAppSDK.WinUI", false, true)]
    [InlineData("", "Windows", "Microsoft.WindowsAppSDK", false, false)]
    [InlineData("true", "", "Microsoft.WindowsAppSDK", false, false)]
    [InlineData("true", "Android", "Microsoft.WindowsAppSDK", false, false)]
    [InlineData("true", "Windows", "Uno.WinUI", false, false)]
    [InlineData("true", "Windows", "Microsoft.Maui.Controls", false, false)]
    [InlineData("true", "Windows", "Microsoft.WindowsAppSDK", true, false)]
    public void RequiresEveryPositiveSignal(string useWinUi, string platform, string package, bool implicitPackage, bool expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Properties = new { UseWinUI = useWinUi, TargetPlatformIdentifier = platform },
            Items = new { PackageReference = new[] { new { Identity = package, IsImplicitlyDefined = implicitPackage.ToString() } } },
        }));
        Assert.Equal(expected, MsBuildCli.IsWinUiEvaluation(json.RootElement));
    }

    [Theory]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk'/>", true)]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk.WindowsDesktop'/>", true)]
    [InlineData("<Project><Sdk Name='Microsoft.NET.Sdk'/></Project>", true)]
    [InlineData("<Project><Import Project='Sdk.props' Sdk='Microsoft.NET.Sdk'/><Import Project='Sdk.targets' Sdk='Microsoft.NET.Sdk'/></Project>", true)]
    [InlineData("<Project Sdk='Uno.Sdk'/>", false)]
    [InlineData("<Project Sdk='Future.Xaml.Sdk'/>", false)]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk;Future.Xaml.Sdk'/>", false)]
    [InlineData("<Project/>", false)]
    public void OnlyKnownProjectSdkShapesQualify(string xml, bool expected) =>
        Assert.Equal(expected, WinUiProjectEligibility.HasSupportedSdk(XElement.Parse(xml)));

    [Theory]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk'/>", true)]
    [InlineData("<Project Sdk='Uno.Sdk'/>", true)]
    [InlineData("<Project Sdk='Microsoft.NET.Sdk.WindowsDesktop'/>", false)]
    [InlineData("<Project Sdk='Future.Xaml.Sdk'/>", false)]
    public void UnoProfileRecognizesUnoSdkShapes(string xml, bool expected) =>
        Assert.Equal(expected, XamlFrameworkProfile.Uno.HasSupportedSdk(XElement.Parse(xml)));

    [Fact]
    public async Task EvaluatesImportsAndSelectedFrameworkWithoutRestoreOrBuildAndInvalidatesEligibility()
    {
        var root = Path.Combine(Path.GetTempPath(), "winui-eligibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Example.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
                    <PackageReference Include="Microsoft.WindowsAppSDK"/>
                  </ItemGroup>
                  <Target Name="UnexpectedBuild" BeforeTargets="Restore;ResolveReferences;CoreCompile">
                    <Error Text="Eligibility must not run targets"/>
                  </Target>
                </Project>
                """);
            var imported = Path.Combine(root, "Directory.Build.props");
            File.WriteAllText(imported, "<Project><PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup></Project>");
            using var resolver = new XamlProjectResolver();
            Assert.True(await resolver.IsSupportedProjectAsync(project));
            Assert.False(File.Exists(Path.Combine(root, "obj", "project.assets.json")));
            File.WriteAllText(imported, "<Project/>");
            resolver.InvalidateAll();
            Assert.False(await resolver.IsSupportedProjectAsync(project));
            File.WriteAllText(imported, "<Project><PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup></Project>");
            resolver.Invalidate(project);
            Assert.True(await resolver.IsSupportedProjectAsync(project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EvaluatesAWindowsTargetOnAnyHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "winui-windows-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Example.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                    <UseWinUI>true</UseWinUI>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.0" /></ItemGroup>
                </Project>
                """);

            var evaluation = MsBuildCli.Evaluate(project, new Dictionary<string, string>());

            Assert.True(evaluation.IsWinUiProject);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnoProfileAcceptsUnoWinUiWithoutAcceptingProGpu()
    {
        var root = Path.Combine(Path.GetTempPath(), "uno-eligibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Uno.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Uno.WinUI" Version="5.0.0" ExcludeAssets="all" /></ItemGroup>
                </Project>
                """);

            Assert.True(WinUiProjectEligibility.Evaluate(
                XamlFrameworkProfile.Uno, project, new Dictionary<string, string>(), CancellationToken.None));

            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><UseProGpuWinUI>true</UseProGpuWinUI></PropertyGroup>
                  <ItemGroup><PackageReference Include="Uno.WinUI" Version="5.0.0" ExcludeAssets="all" /></ItemGroup>
                </Project>
                """);
            Assert.False(WinUiProjectEligibility.Evaluate(
                XamlFrameworkProfile.Uno, project, new Dictionary<string, string>(), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnoProfileResolvesControlsFromTheUnoSdkReferenceClosure()
    {
        var repositoryRoot = FindOpenDevelopRoot();
        if (repositoryRoot is null) return;
        var project = Path.Combine(repositoryRoot, "src", "Samples", "UnoXamlSample", "UnoXamlSample.csproj");
        var page = Path.Combine(repositoryRoot, "src", "Samples", "UnoXamlSample", "MainPage.xaml");

        Assert.True(File.Exists(project), $"Expected the OpenDevelop Uno sample at '{project}'.");
        Assert.True(File.Exists(page), $"Expected the OpenDevelop Uno page at '{page}'.");

        using var resolver = new XamlProjectResolver(XamlFrameworkProfile.Uno);
        var resolution = await resolver.ResolveFrameworkAsync(page);
        Assert.NotNull(resolution);
        Assert.NotNull(resolution!.Compilation.GetTypeByMetadataName("Microsoft.UI.Xaml.Controls.Grid"));
        var typeSystem = XamlTypeSystem.FromResolution(resolution);
        Assert.Contains(typeSystem.GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "Microsoft.UI.Xaml.Controls.Grid");

        var fullResolution = await resolver.ResolveAsync(page);
        Assert.NotNull(fullResolution);
        var fullTypeSystem = XamlTypeSystem.FromResolution(fullResolution!);
        Assert.Contains(fullTypeSystem.GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "Microsoft.UI.Xaml.Controls.Grid");
    }

    [Fact]
    public async Task WinUiProfileProvidesMinimalControlsForTheDeliberateNoPackageFixture()
    {
        var repositoryRoot = FindOpenDevelopRoot();
        if (repositoryRoot is null) return;
        var project = Path.Combine(repositoryRoot, "src", "Samples", "MicrosoftWinUISample", "MicrosoftWinUISample.csproj");
        var page = Path.Combine(repositoryRoot, "src", "Samples", "MicrosoftWinUISample", "MainPage.xaml");

        using var resolver = new XamlProjectResolver(XamlFrameworkProfile.WinUi);
        Assert.True(await resolver.IsSupportedProjectAsync(project));
        var resolution = await resolver.ResolveFrameworkAsync(page);
        Assert.NotNull(resolution);
        Assert.NotNull(resolution!.Compilation.GetTypeByMetadataName("Microsoft.UI.Xaml.Controls.Grid"));
        Assert.Contains(XamlTypeSystem.FromResolution(resolution, XamlFrameworkProfile.WinUi).GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "Microsoft.UI.Xaml.Controls.Grid");
        Assert.DoesNotContain(XamlTypeSystem.FromResolution(resolution, XamlFrameworkProfile.Wpf).GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "Microsoft.UI.Xaml.Controls.Grid");
    }

    [Fact]
    public async Task WpfProfileResolvesTheMicrosoftWpfSampleWithoutWindowsDesktopMetadata()
    {
        var repositoryRoot = FindOpenDevelopRoot();
        if (repositoryRoot is null) return;
        var project = Path.Combine(repositoryRoot, "src", "Samples", "MicrosoftWpfSample", "MicrosoftWpfSample.csproj");
        var page = Path.Combine(repositoryRoot, "src", "Samples", "MicrosoftWpfSample", "MainWindow.xaml");

        using var resolver = new XamlProjectResolver(XamlFrameworkProfile.Wpf);
        Assert.True(await resolver.IsSupportedProjectAsync(project));
        var resolution = await resolver.ResolveFrameworkAsync(page);
        Assert.NotNull(resolution);
        Assert.NotNull(resolution!.Compilation.GetTypeByMetadataName("System.Windows.Controls.Grid"));
        Assert.NotNull(resolution.Compilation.GetTypeByMetadataName("System.Windows.Controls.Border"));
        Assert.Contains(XamlTypeSystem.FromResolution(resolution, XamlFrameworkProfile.LibreWpf).GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "System.Windows.Controls.Grid");
    }

    [Fact]
    public async Task LibreWpfProfileResolvesControlsFromTheLibreWpfReferenceClosure()
    {
        var repositoryRoot = FindOpenDevelopRoot();
        if (repositoryRoot is null) return;
        var project = Path.Combine(repositoryRoot, "src", "Samples", "LibreWpfSample", "sample.csproj");
        var page = Path.Combine(repositoryRoot, "src", "Samples", "LibreWpfSample", "MainWindow.xaml");

        using var resolver = new XamlProjectResolver(XamlFrameworkProfile.LibreWpf);
        Assert.True(await resolver.IsSupportedProjectAsync(project));
        var resolution = await resolver.ResolveFrameworkAsync(page);
        Assert.NotNull(resolution);
        Assert.NotNull(resolution!.Compilation.GetTypeByMetadataName("System.Windows.Controls.Grid"));
        Assert.Contains(XamlTypeSystem.FromResolution(resolution).GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "System.Windows.Controls.Grid");
    }

    [Fact]
    public async Task ProGpuProfileProvidesMinimalWinUiControlsForTheProGpuFixture()
    {
        var repositoryRoot = FindOpenDevelopRoot();
        if (repositoryRoot is null) return;
        var project = Path.Combine(repositoryRoot, "src", "Samples", "ProGpuWinUISample", "ProGpuWinUISample.csproj");
        var page = Path.Combine(repositoryRoot, "src", "Samples", "ProGpuWinUISample", "MainPage.xaml");

        using var resolver = new XamlProjectResolver(XamlFrameworkProfile.ProGpu);
        Assert.True(await resolver.IsSupportedProjectAsync(project));
        var resolution = await resolver.ResolveFrameworkAsync(page);
        Assert.NotNull(resolution);
        Assert.NotNull(resolution!.Compilation.GetTypeByMetadataName("Microsoft.UI.Xaml.Controls.Grid"));
        Assert.Contains(XamlTypeSystem.FromResolution(resolution).GetTypes(XamlTypeSystem.PresentationNamespace),
            type => type.ToDisplayString() == "Microsoft.UI.Xaml.Controls.Grid");
    }

    [Fact]
    public void UnknownSdkIsRejectedWithoutResolvingOrExecutingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "winui-unknown-sdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Example.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Example.Unknown.Xaml.Sdk/1.0.0">
                  <PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.WindowsAppSDK"/></ItemGroup>
                </Project>
                """);
            Assert.False(WinUiProjectEligibility.Evaluate(project, new Dictionary<string, string>(), CancellationToken.None));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? FindOpenDevelopRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenDevelop.Mvp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
