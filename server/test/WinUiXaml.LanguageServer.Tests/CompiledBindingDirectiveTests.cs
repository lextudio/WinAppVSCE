using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WinUiXaml.LanguageServer;
using WinUiXaml.Workspace;
using WinUiXaml.Xaml;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// <c>x:DataType</c> and <c>x:CompileBindings</c> are directives of the LibreWPF XAML source generator (compiled bindings). They are
/// not part of stock XAML, so the language service must neither report them as unknown nor misread the binding they scope.
/// </summary>
public sealed class CompiledBindingDirectiveTests
{
    private const string Source = """
        namespace System.Windows { public class FrameworkElement { } public class Window : FrameworkElement { } }
        namespace System.Windows.Controls
        {
            public class Panel : System.Windows.FrameworkElement { }
            public class Grid : Panel { }
            public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } = ""; }
        }
        namespace App { public partial class MainWindow : System.Windows.Window { } }
        """;

    private static string[] Codes(string xaml, bool implicitNamespaces) =>
        XamlValidator.Validate(
            new TextDocument("file:///a.xaml", xaml, null, implicitNamespaces),
            XamlTypeSystem.FromCompilation(
                CSharpCompilation.Create("App", new[] { CSharpSyntaxTree.ParseText(Source) },
                    new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
                ImmutableArray<IAssemblySymbol>.Empty, XamlFrameworkProfile.Wpf))
            .Select(d => d.Code ?? "").ToArray();

    [Theory]
    [InlineData("x:DataType=\"x:String\"")]
    [InlineData("x:CompileBindings=\"True\"")]
    [InlineData("x:DataType=\"x:String\" x:CompileBindings=\"False\"")]
    public void TheDirectivesOnTheRoot_ProduceNoDiagnostic(string directives)
    {
        var xaml = "<Window x:Class=\"App.MainWindow\" " + directives + "><Grid><TextBlock Text=\"{Binding Length}\" /></Grid></Window>";

        Assert.Empty(Codes(xaml, implicitNamespaces: true));
    }

    [Fact]
    public void TheRootsAttributesAreReallyChecked_AnUnknownOneIsReported()
    {
        // The control: with it a clean result for the directives above means something.
        var xaml = "<Window x:Class=\"App.MainWindow\" Bogus=\"1\"><Grid /></Window>";

        Assert.Equal(new[] { "WXAML0003" }, Codes(xaml, implicitNamespaces: true));
    }
}
