using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WinUiXaml.LanguageServer;
using WinUiXaml.Workspace;
using WinUiXaml.Xaml;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// <c>{cs: expression}</c> is a value of the LibreWPF XAML source generator (C# expressions): the generator compiles the expression
/// and reports its own diagnostics (LWXSG0004 when the feature is off) through the build. The language service must stay out of
/// the way: a <c>{cs:}</c> value is neither an unknown markup extension nor an undeclared prefix, in an old-style document
/// (xmlns declared) and in a new-style one (implicit namespaces).
/// </summary>
public sealed class CSharpExpressionValueTests
{
    // Stand-ins for the WPF types the presentation namespace maps to by convention (System.Windows*), so that elements and
    // attributes RESOLVE and the validator really analyses the attribute values (an unresolved element returns before that).
    private const string Source = """
        namespace System.Windows { public class FrameworkElement { } public class Window : FrameworkElement { } }
        namespace System.Windows.Controls
        {
            public class Panel : System.Windows.FrameworkElement { }
            public class Grid : Panel { }
            public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } = ""; public object? Tag { get; set; } }
        }
        namespace App { public partial class MainWindow : System.Windows.Window { public int Count => 1; } }
        """;

    private static XamlTypeSystem TypeSystem() => XamlTypeSystem.FromCompilation(
        CSharpCompilation.Create("App", new[] { CSharpSyntaxTree.ParseText(Source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
        ImmutableArray<IAssemblySymbol>.Empty, XamlFrameworkProfile.Wpf);

    private static string[] Codes(string xaml, bool implicitNamespaces) =>
        XamlValidator.Validate(new TextDocument("file:///a.xaml", xaml, null, implicitNamespaces), TypeSystem())
            .Select(d => d.Code ?? "").ToArray();

    private static string NewStyle(string attributes) =>
        "<Window x:Class=\"App.MainWindow\"><Grid><TextBlock " + attributes + " /></Grid></Window>";

    private static string OldStyle(string attributes) =>
        "<Window x:Class=\"App.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
        + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Grid><TextBlock " + attributes + " /></Grid></Window>";

    [Fact]
    public void TheDocumentsAreReallyAnalysed_AnUnknownPropertyIsReported()
    {
        // The control for every assertion below: elements resolve and attributes are checked, so "no diagnostic" is meaningful.
        Assert.NotEmpty(Codes(NewStyle("Bogus=\"1\""), implicitNamespaces: true));
        Assert.NotEmpty(Codes(OldStyle("Bogus=\"1\""), implicitNamespaces: false));
        Assert.Empty(Codes(NewStyle("Text=\"plain\""), implicitNamespaces: true));
        Assert.Empty(Codes(OldStyle("Text=\"plain\""), implicitNamespaces: false));
    }

    [Theory]
    [InlineData("{cs: &quot;ready&quot;}")]
    [InlineData("{cs: Count + 1}")]
    [InlineData("{cs: Count > 0 ? &quot;a&quot; : &quot;b&quot;}")]
    public void ACsValue_ProducesNoXamlDiagnostic_InNewAndOldStyleDocuments(string value)
    {
        Assert.Empty(Codes(NewStyle("Text=\"" + value + "\""), implicitNamespaces: true));
        Assert.Empty(Codes(OldStyle("Text=\"" + value + "\""), implicitNamespaces: false));
        Assert.Empty(Codes(OldStyle("Text=\"" + value + "\""), implicitNamespaces: true));
    }

    [Fact]
    public void ACsValueDoesNotHideARealErrorOnTheSameElement()
    {
        // An unknown property beside a {cs:} value is still reported, with exactly that one diagnostic.
        var codes = Codes(NewStyle("Text=\"{cs: Count}\" Bogus=\"1\""), implicitNamespaces: true);

        Assert.Equal(new[] { "WXAML0003" }, codes);
    }
}
