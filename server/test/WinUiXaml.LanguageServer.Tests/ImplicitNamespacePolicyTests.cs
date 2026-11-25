using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WinUiXaml.LanguageServer;
using WinUiXaml.Workspace;
using WinUiXaml.Xaml;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// Which documents the language service reads with implicit namespaces (Simpler XAML): those of a LibreWPF project that turned the
/// XAML source generator on and did not turn the feature off. BAML projects and every other framework profile keep the strict reading.
/// </summary>
public sealed class ImplicitNamespacePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "implicit-ns-" + Guid.NewGuid().ToString("N"));

    public ImplicitNamespacePolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Project(string properties, string? directoryBuildProps = null, string subdirectory = "")
    {
        var directory = Path.Combine(_root, subdirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project Sdk=\"LibreWPF.Sdk\"><PropertyGroup>" + properties + "</PropertyGroup></Project>");
        if (directoryBuildProps is not null)
        {
            File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project><PropertyGroup>" + directoryBuildProps + "</PropertyGroup></Project>");
        }

        return Path.Combine(directory, "MainWindow.xaml");
    }

    private static bool Enabled(string path) => ImplicitNamespacePolicy.IsEnabled(XamlFrameworkProfile.LibreWpf, path);

    private static bool ExpressionsOn(string path, XamlFrameworkProfile? profile = null) =>
        ImplicitNamespacePolicy.ExpressionsEnabled(profile ?? XamlFrameworkProfile.LibreWpf, path);

    [Fact]
    public void CSharpExpressions_NeedTheXsgSwitch_AndFollowIt()
    {
        Assert.False(ExpressionsOn(Project("")));
        Assert.False(ExpressionsOn(Project("<LibreWpfCSharpExpressions>true</LibreWpfCSharpExpressions>")));
        Assert.False(ExpressionsOn(Project("<UseLibreWpfXamlSourceGenerator>false</UseLibreWpfXamlSourceGenerator>")));
        Assert.True(ExpressionsOn(Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>")));
        Assert.False(ExpressionsOn(Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator><LibreWpfCSharpExpressions>false</LibreWpfCSharpExpressions>")));
    }

    [Fact]
    public void CSharpExpressions_AreIndependentOfSimplerXaml()
    {
        var path = Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator><LibreWpfSimplerXaml>false</LibreWpfSimplerXaml>");
        Assert.True(ExpressionsOn(path));
        Assert.False(Enabled(path));
    }

    [Fact]
    public void CSharpExpressions_AreOffForEveryOtherProfile_AndForAmbiguousOrMissingProjects()
    {
        var path = Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>");
        Assert.False(ExpressionsOn(path, XamlFrameworkProfile.WinUi));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "Other.csproj"), "<Project />");
        Assert.False(ExpressionsOn(path));
        Assert.False(ImplicitNamespacePolicy.ExpressionsEnabled(XamlFrameworkProfile.LibreWpf, null));
    }

    [Fact]
    public void AnEditedProjectFile_ChangesTheExpressionsAnswer()
    {
        var path = Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>");
        Assert.True(ExpressionsOn(path));
        var project = Path.Combine(Path.GetDirectoryName(path)!, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"LibreWPF.Sdk\"><PropertyGroup><UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator><LibreWpfCSharpExpressions>false</LibreWpfCSharpExpressions></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddMinutes(5));
        Assert.False(ExpressionsOn(path));
    }

    [Fact]
    public void ABamlProject_IsStrict()
    {
        Assert.False(Enabled(Project("")));
        Assert.False(Enabled(Project("<UseLibreWpfXamlSourceGenerator>false</UseLibreWpfXamlSourceGenerator>")));
    }

    [Fact]
    public void AFeatureWithoutTheOverallSwitch_DoesNothing()
    {
        Assert.False(Enabled(Project("<LibreWpfSimplerXaml>true</LibreWpfSimplerXaml>")));
    }

    [Fact]
    public void AnXsgProject_IsImplicit_UnlessSimplerXamlIsOff()
    {
        Assert.True(Enabled(Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>")));
        Assert.True(Enabled(Project("<UseLibreWpfXamlSourceGenerator>True</UseLibreWpfXamlSourceGenerator><LibreWpfSimplerXaml>true</LibreWpfSimplerXaml>")));
        Assert.False(Enabled(Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator><LibreWpfSimplerXaml>false</LibreWpfSimplerXaml>")));
    }

    [Fact]
    public void TheOtherFeature_DoesNotMatter()
    {
        Assert.True(Enabled(Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator><LibreWpfCSharpExpressions>false</LibreWpfCSharpExpressions>")));
    }

    [Fact]
    public void ADeclarationInADirectoryBuildPropsAbove_Counts_AndTheProjectOverridesIt()
    {
        Assert.True(Enabled(Project("", "<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>", "src")));
        Assert.False(Enabled(Project("<LibreWpfSimplerXaml>false</LibreWpfSimplerXaml>", "<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>", "src2")));
    }

    [Fact]
    public void AConditionalProperty_IsNotSeen()
    {
        Assert.False(Enabled(Project("<UseLibreWpfXamlSourceGenerator Condition=\"'$(Configuration)' == 'Debug'\">true</UseLibreWpfXamlSourceGenerator>")));
    }

    [Fact]
    public void APropertyInsideAChooseOrWhen_IsNotSeen()
    {
        var path = Project("");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "App.csproj"),
            "<Project Sdk=\"LibreWPF.Sdk\"><Choose><When Condition=\"true\"><PropertyGroup><UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator></PropertyGroup></When></Choose></Project>");

        Assert.False(Enabled(path));
    }

    [Fact]
    public void EveryOtherFrameworkProfile_IsStrictEvenForAnXsgProject()
    {
        var path = Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>");

        foreach (var profile in new[] { XamlFrameworkProfile.WinUi, XamlFrameworkProfile.Uno, XamlFrameworkProfile.Wpf, XamlFrameworkProfile.ProGpu })
        {
            Assert.False(ImplicitNamespacePolicy.IsEnabled(profile, path), profile.Id);
        }
    }

    [Fact]
    public void ADirectoryWithSeveralProjectFiles_IsAmbiguousAndStrict()
    {
        var path = Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>");
        Assert.True(Enabled(path));

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "Other.csproj"), "<Project Sdk=\"LibreWPF.Sdk\" />");

        Assert.False(Enabled(path));
    }

    [Fact]
    public void NoProjectOrNoPath_IsStrict()
    {
        Assert.False(Enabled(Path.Combine(_root, "nowhere", "MainWindow.xaml")));
        Assert.False(ImplicitNamespacePolicy.IsEnabled(XamlFrameworkProfile.LibreWpf, null));
    }

    [Fact]
    public void AnEditedProjectFile_IsReadAgain()
    {
        var path = Project("");
        Assert.False(Enabled(path));

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "App.csproj"),
            "<Project Sdk=\"LibreWPF.Sdk\"><PropertyGroup><UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(path)!, "App.csproj"), DateTime.UtcNow.AddMinutes(1));

        Assert.True(Enabled(path));
    }

    private const string NewStyleWindow = "<Window x:Class=\"App.MainWindow\"><Grid><Button x:Name=\"Go\" /></Grid></Window>";

    private static XamlTypeSystem EmptyTypeSystem() => XamlTypeSystem.FromCompilation(
        CSharpCompilation.Create("TestApp", new[] { CSharpSyntaxTree.ParseText("namespace App { }") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
        ImmutableArray<IAssemblySymbol>.Empty);

    private static bool ReportsAnUndeclaredPrefix(TextDocument document) =>
        XamlValidator.Validate(document, EmptyTypeSystem()).Any(d => d.Code == XamlValidator.UndeclaredPrefixCode);

    [Fact]
    public void TheMissingXmlnsDiagnostic_IsSuppressedOnlyForAnImplicitDocument()
    {
        Assert.True(ReportsAnUndeclaredPrefix(new TextDocument("file:///a.xaml", NewStyleWindow)));
        Assert.False(ReportsAnUndeclaredPrefix(new TextDocument("file:///a.xaml", NewStyleWindow, null, implicitNamespaces: true)));
    }

    private static byte[] Frame(string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        return System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
    }

    private static string Notification(string method, object @params) =>
        System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });

    /// <summary>Sends the given notifications through the server's own handlers and returns the document it holds afterwards.</summary>
    private static async Task<TextDocument> RunAsync(XamlFrameworkProfile profile, string path, bool change, string text)
    {
        var uri = new Uri(path).AbsoluteUri;
        var frames = Frame(Notification("textDocument/didOpen", new { textDocument = new { uri, languageId = "xaml", version = 1, text } }));
        if (change)
        {
            frames = frames.Concat(Frame(Notification("textDocument/didChange", new
            {
                textDocument = new { uri, version = 2 },
                contentChanges = new[] { new { text = text + " " } },
            }))).ToArray();
        }

        await using var input = new MemoryStream(frames);
        await using var output = new MemoryStream();
        var connection = new Lsp.JsonRpcConnection(input, output);
        using var resolver = new XamlProjectResolver(profile);
        var server = new XamlLanguageServer(connection, resolver);

        await connection.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var document = server.GetOpenDocument(uri)!;
        Assert.Equal(change ? 2 : 1, document.Version);
        return document;
    }

    private static bool ResolvesX(TextDocument document) => document.Parsed.Root!.NamespaceScope.TryResolvePrefix("x", out _);

    [Theory]
    [InlineData(false)] // didOpen alone: the document the open handler built
    [InlineData(true)]  // didOpen then didChange: the document the change handler built
    public async Task TheServersOpenAndChangeHandlers_ApplyThePolicyOfTheProfileAndTheProject(bool change)
    {
        var xsgProject = Project("<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>", subdirectory: "xsg");
        var bamlProject = Project("", subdirectory: "baml");

        // LibreWPF + XSG project: implicit.
        Assert.True(ResolvesX(await RunAsync(XamlFrameworkProfile.LibreWpf, xsgProject, change, NewStyleWindow)));
        // LibreWPF + BAML project: strict.
        Assert.False(ResolvesX(await RunAsync(XamlFrameworkProfile.LibreWpf, bamlProject, change, NewStyleWindow)));
        // Another framework profile, even for the XSG project's file: strict.
        Assert.False(ResolvesX(await RunAsync(XamlFrameworkProfile.WinUi, xsgProject, change, NewStyleWindow)));
    }

    [Fact]
    public void ADocumentOfAnXsgProject_ReportsNoMissingNamespaceOnlyWhenImplicit()
    {
        var xaml = "<Window x:Class=\"App.MainWindow\"><Grid /></Window>";

        Assert.False(new TextDocument("file:///a.xaml", xaml).Parsed.Root!.NamespaceScope.TryResolvePrefix("x", out _));
        Assert.True(new TextDocument("file:///a.xaml", xaml, null, implicitNamespaces: true).Parsed.Root!.NamespaceScope.TryResolvePrefix("x", out _));
    }
}
