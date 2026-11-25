using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WinUiXaml.LanguageServer;
using WinUiXaml.Workspace;
using Xunit;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// The errors the XAML source generator gives for a C# expression value (LWXSG0011, LWXSG0012), shown in the editor while typing
/// with the same ids as the build.
/// </summary>
public sealed class ExpressionDiagnosticsTests
{
    private const string Source = """
        namespace System.Windows { public class DependencyProperty { } public class FrameworkElement { } public class Window : FrameworkElement { } }
        namespace System.Windows.Controls
        {
            public class Panel : System.Windows.FrameworkElement { }
            public class Grid : Panel { }
            public class Button : System.Windows.FrameworkElement
            {
                public static readonly System.Windows.DependencyProperty ContentProperty = new();
                public object Content { get; set; }
                public string Tag2 { get; set; } = "";
                public event System.EventHandler Click;
            }
        }
        namespace App
        {
            public class User { public string Name { get; set; } = ""; public string Email { get; set; } = ""; }
            public partial class MainWindow : System.Windows.Window { public string Email => ""; public string Greeting => ""; }
        }
        """;

    private static string[] Codes(string attributes, bool expressions = true)
    {
        var text = "<Window x:Class=\"App.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:App\">"
            + "<Grid x:DataType=\"local:User\"><Button " + attributes + " /></Grid></Window>";
        var compilation = CSharpCompilation.Create("App", new[] { CSharpSyntaxTree.ParseText(Source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var typeSystem = XamlTypeSystem.FromCompilation(compilation, ImmutableArray<IAssemblySymbol>.Empty, XamlFrameworkProfile.Wpf);
        var doc = new TextDocument("file:///a.xaml", text, null, implicitNamespaces: false, csharpExpressions: expressions);
        return XamlValidator.Validate(doc, typeSystem).Select(d => d.Code ?? "").Where(c => c.StartsWith("LWXSG")).ToArray();
    }

    [Fact]
    public void AnAsyncLambda_IsAnError()
    {
        Assert.Equal(new[] { "LWXSG0011" }, Codes("Click=\"{async (s, e) => await Save()}\""));
    }

    [Fact]
    public void ANameOnBothThePageAndTheDataType_IsAnError_ForABindableProperty()
    {
        Assert.Equal(new[] { "LWXSG0012" }, Codes("Content=\"{Email}\""));
    }

    [Theory]
    [InlineData("Content=\"{Name}\"")]            // the DataContext only
    [InlineData("Content=\"{Greeting}\"")]        // the page only
    [InlineData("Content=\"{this.Email}\"")]      // explicit page
    [InlineData("Content=\"{.Email}\"")]          // explicit DataContext
    [InlineData("Tag2=\"{Email}\"")]              // a CLR property is never a binding
    [InlineData("Content=\"{Email + '!'}\"")]     // not a simple path
    [InlineData("Click=\"{(s, e) => Save()}\"")]  // a lambda is fine
    public void EveryOtherForm_IsQuiet(string attributes)
    {
        Assert.Empty(Codes(attributes));
    }

    [Fact]
    public void WithExpressionsOff_NothingIsReported()
    {
        Assert.Empty(Codes("Content=\"{Email}\"", expressions: false));
    }
}
