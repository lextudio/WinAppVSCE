using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WinUiXaml.LanguageServer;
using WinUiXaml.Workspace;
using Xunit;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// Simpler XAML as in .NET MAUI: a type of a namespace the project's own <c>global using</c> directives name is found without an
/// xmlns declaration. Only the LibreWPF profile (the XAML source generator) has it.
/// </summary>
public sealed class GlobalUsingTypeTests
{
    private const string Source = """
        namespace System.Windows { public class FrameworkElement { } public class Window : FrameworkElement { } }
        namespace System.Windows.Controls { public class Panel : System.Windows.FrameworkElement { } public class Grid : Panel { } }
        namespace Sample.Controls { public class Fancy : System.Windows.FrameworkElement { public string Caption { get; set; } = ""; } }
        namespace Sample { public partial class MainWindow : System.Windows.Window { } }
        """;

    private static XamlTypeSystem Types(string usings, XamlFrameworkProfile profile)
    {
        var compilation = CSharpCompilation.Create("Sample", new[] { CSharpSyntaxTree.ParseText(usings + Source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return XamlTypeSystem.FromCompilation(compilation, ImmutableArray<IAssemblySymbol>.Empty, profile);
    }

    [Fact]
    public void LibreWpf_FindsATypeOfAGlobalUsingNamespace_InThePresentationNamespace()
    {
        var types = Types("global using Sample.Controls;\n", XamlFrameworkProfile.LibreWpf);
        Assert.Equal("Fancy", types.ResolveType(XamlTypeSystem.PresentationNamespace, "Fancy")?.Name);
    }

    [Theory]
    [InlineData("")]                                                  // no global using
    [InlineData("using Sample.Controls;\n")]                          // an ordinary using
    [InlineData("global using Alias = Sample.Controls;\n")]           // an alias
    [InlineData("global using static Sample.Controls.Fancy;\n")]      // a static using
    public void WithoutANamespaceGlobalUsing_NothingIsFound(string usings)
    {
        Assert.Null(Types(usings, XamlFrameworkProfile.LibreWpf).ResolveType(XamlTypeSystem.PresentationNamespace, "Fancy"));
    }

    [Fact]
    public void ALibreWpfProjectWithSimplerXamlOff_KeepsTheStrictLookup()
    {
        // The language server passes the policy's answer (Simpler XAML on or off for the project); a BAML project is off.
        var compilation = CSharpCompilation.Create("Sample", new[] { CSharpSyntaxTree.ParseText("global using Sample.Controls;\n" + Source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var off = XamlTypeSystem.FromCompilation(compilation, ImmutableArray<IAssemblySymbol>.Empty, XamlFrameworkProfile.LibreWpf, globalUsingTypes: false);
        Assert.Null(off.ResolveType(XamlTypeSystem.PresentationNamespace, "Fancy"));
        var on = XamlTypeSystem.FromCompilation(compilation, ImmutableArray<IAssemblySymbol>.Empty, XamlFrameworkProfile.LibreWpf, globalUsingTypes: true);
        Assert.Equal("Fancy", on.ResolveType(XamlTypeSystem.PresentationNamespace, "Fancy")?.Name);
    }

    [Fact]
    public void MicrosoftWpf_DoesNotHaveIt()
    {
        var types = Types("global using Sample.Controls;\n", XamlFrameworkProfile.Wpf);
        Assert.Null(types.ResolveType(XamlTypeSystem.PresentationNamespace, "Fancy"));
    }

    [Fact]
    public void ThePresentationNamespace_StillWinsForItsOwnTypes()
    {
        var types = Types("global using Sample.Controls;\n", XamlFrameworkProfile.LibreWpf);
        Assert.Equal("System.Windows.Controls", types.ResolveType(XamlTypeSystem.PresentationNamespace, "Grid")?.ContainingNamespace.ToDisplayString());
    }

    [Fact]
    public void TheValidator_DoesNotReportAnUnknownType_ForAGlobalUsingType()
    {
        var types = Types("global using Sample.Controls;\n", XamlFrameworkProfile.LibreWpf);
        var text = "<Window x:Class=\"Sample.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Grid><Fancy Caption=\"hi\" /></Grid></Window>";
        var doc = new TextDocument("file:///a.xaml", text, null, implicitNamespaces: false, csharpExpressions: true);
        var codes = XamlValidator.Validate(doc, types).Select(d => d.Code).ToArray();
        Assert.DoesNotContain(XamlValidator.UnknownTypeCode, codes);
        Assert.DoesNotContain(XamlValidator.UnknownAttributeCode, codes);
    }

    [Fact]
    public void ElementNameCompletion_OffersAGlobalUsingType()
    {
        var types = Types("global using Sample.Controls;\n", XamlFrameworkProfile.LibreWpf);
        var text = "<Window x:Class=\"Sample.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Grid><Fa";
        var doc = new TextDocument("file:///a.xaml", text, null, implicitNamespaces: false, csharpExpressions: true);
        var labels = CompletionProvider.Provide(doc, text.Length, types).Items.Select(i => i.Label).ToArray();
        Assert.Contains("Fancy", labels);
    }
}
