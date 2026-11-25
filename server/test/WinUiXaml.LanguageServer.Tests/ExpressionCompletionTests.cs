using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WinUiXaml.LanguageServer;
using WinUiXaml.Workspace;
using Xunit;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// Completion inside a C# expression value (<c>{Name}</c>, <c>{= User.Ci</c>; the syntax of .NET MAUI that the LibreWPF XAML source
/// generator follows): names come from the nearest <c>x:DataType</c> (a binding) and from the page's x:Class, in the order the generator looks.
/// </summary>
public sealed class ExpressionCompletionTests
{
    private const string Source = """
        namespace System.Windows { public class FrameworkElement { } public class Window : FrameworkElement { } }
        namespace System.Windows.Controls
        {
            public class Panel : System.Windows.FrameworkElement { }
            public class Grid : Panel { }
            public class TextBlock : System.Windows.FrameworkElement { public string Text { get; set; } = ""; }
        }
        namespace App
        {
            public class Address { public string City { get; set; } = ""; public string Zip { get; set; } = ""; }
            public class User { public string Name { get; set; } = ""; public int Age { get; set; } public Address Home { get; set; } = new(); }
            public static class Helper { public static int Twice(int x) => x * 2; public static string Label => ""; public static int Hidden = 0; }
            public partial class MainWindow : System.Windows.Window
            {
                public int Count => 1;
                public string Title2 { get; set; } = "";
                public string Name2 => "";
                public void Save() { }
            }
        }
        """;

    private static (XamlTypeSystem TypeSystem, INamedTypeSymbol Page) Types()
    {
        var compilation = CSharpCompilation.Create("App", new[] { CSharpSyntaxTree.ParseText(Source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (XamlTypeSystem.FromCompilation(compilation, ImmutableArray<IAssemblySymbol>.Empty, XamlFrameworkProfile.Wpf),
            compilation.GetTypeByMetadataName("App.MainWindow")!);
    }

    // The caret is written as '|'.
    private static string[] Complete(string attribute, bool dataType = true, bool expressions = true)
    {
        int bar = attribute.IndexOf('|');
        var value = attribute.Remove(bar, 1);
        var grid = dataType ? "<Grid x:DataType=\"local:User\">" : "<Grid>";
        var prefix = "<Window x:Class=\"App.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:App\">" + grid + "<TextBlock Text=\"";
        var text = prefix + value + "\" /></Grid></Window>";
        var doc = new TextDocument("file:///a.xaml", text, null, implicitNamespaces: false, csharpExpressions: expressions);
        var (typeSystem, page) = Types();
        var list = CompletionProvider.Provide(doc, prefix.Length + bar, typeSystem, page);
        return list.Items.Select(i => i.Label).ToArray();
    }

    [Fact]
    public void ABareName_OffersTheDataContextAndThePage()
    {
        var labels = Complete("{Na|}");
        Assert.Contains("Name", labels);   // User, from x:DataType
        Assert.Contains("Name2", labels);  // MainWindow, the page
        Assert.DoesNotContain("Age", labels);
    }

    [Fact]
    public void TheCaretOnTheBraces_IsNotAnExpressionPosition()
    {
        // Outside the code the ordinary attribute-value completion applies; the expression names are not offered there.
        Assert.DoesNotContain("Name2", Complete("|{Na}"));
        Assert.DoesNotContain("Name2", Complete("{Na}|"));
    }

    [Fact]
    public void AnExplicitExpression_CompletesTheSameWay()
    {
        var labels = Complete("{= Co|}");
        Assert.Contains("Count", labels);
    }

    [Fact]
    public void ThisDot_OffersThePageOnly()
    {
        var labels = Complete("{this.|}");
        Assert.Contains("Count", labels);
        Assert.Contains("Save", labels);
        Assert.DoesNotContain("Name", labels);
        Assert.DoesNotContain("Age", labels);
    }

    [Fact]
    public void ALeadingDot_OffersTheDataContextOnly()
    {
        var labels = Complete("{.|}");
        Assert.Contains("Name", labels);
        Assert.Contains("Age", labels);
        Assert.DoesNotContain("Count", labels);
        Assert.DoesNotContain("this", labels);
    }

    [Fact]
    public void ADottedPath_FollowsTheTypesOfThePath()
    {
        var labels = Complete("{Home.Ci|}");
        Assert.Equal(new[] { "City" }, labels);
        Assert.Contains("Zip", Complete("{Home.|}"));
    }

    [Fact]
    public void AfterAnOperator_TheSecondOperandCompletesToo()
    {
        Assert.Contains("Age", Complete("{Count GT 0 AND Ag|}"));
        Assert.Contains("Age", Complete("{= Count + Ag|}"));
    }

    [Fact]
    public void WithoutADataType_OnlyThePageIsOffered()
    {
        var labels = Complete("{Na|}", dataType: false);
        Assert.Contains("Name2", labels);
        Assert.DoesNotContain("Name", labels);
    }

    [Fact]
    public void KeywordsAreOffered_ForABareName()
    {
        Assert.Contains("this", Complete("{th|}"));
        Assert.Contains("null", Complete("{= nu|}"));
    }

    [Theory]
    [InlineData("{= 'Na|'}")]
    [InlineData("{$'Total: Na|'}")]
    public void InsideAStringLiteral_NothingIsOffered(string attribute)
    {
        Assert.Empty(Complete(attribute));
    }

    [Fact]
    public void InAnInterpolationHole_NamesComplete()
    {
        Assert.Contains("Age", Complete("{$'Age: {Ag|}'}"));
    }

    [Fact]
    public void WithExpressionsOff_TheUsualCompletionApplies()
    {
        // A markup extension name is completed instead, not a member.
        Assert.DoesNotContain("Name2", Complete("{Na|}", expressions: false));
    }

    // Hover and go to definition: the member under the caret, found where the generator looks.
    private static string? MemberAt(string attribute, bool dataType = true)
    {
        int bar = attribute.IndexOf('|');
        var value = attribute.Remove(bar, 1);
        var grid = dataType ? "<Grid x:DataType=\"local:User\">" : "<Grid>";
        var prefix = "<Window x:Class=\"App.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:App\">" + grid + "<TextBlock Text=\"";
        var text = prefix + value + "\" /></Grid></Window>";
        var doc = new TextDocument("file:///a.xaml", text, null, implicitNamespaces: false, csharpExpressions: true);
        var (typeSystem, page) = Types();
        return CompletionProvider.ResolveExpressionMemberAt(doc, prefix.Length + bar, typeSystem, page) is { } hit
            ? hit.Symbol.ContainingType.Name + "." + hit.Symbol.Name + "@" + text.Substring(hit.Span.Start, hit.Span.Length)
            : null;
    }

    [Theory]
    [InlineData("{Na|me + 1}", "User.Name@Name")]
    [InlineData("{= Cou|nt}", "MainWindow.Count@Count")]
    [InlineData("{Home.Ci|ty}", "Address.City@City")]
    [InlineData("{Ho|me.City}", "User.Home@Home")]
    [InlineData("{this.Sa|ve()}", "MainWindow.Save@Save")]
    [InlineData("{.Ag|e}", "User.Age@Age")]
    [InlineData("{Count GT Ag|e}", "User.Age@Age")]
    public void TheMemberUnderTheCaret_IsFoundWhereTheGeneratorLooks(string attribute, string expected)
    {
        Assert.Equal(expected, MemberAt(attribute));
    }

    [Theory]
    [InlineData("{= 'Na|me'}")]
    [InlineData("{Missing|Name}")]
    [InlineData("{1 + |2}")]
    public void NoMember_ForAStringAnUnknownNameOrANumber(string attribute)
    {
        Assert.Null(MemberAt(attribute));
    }

    [Fact]
    public void ATypeName_OffersItsStaticMembers()
    {
        var labels = Complete("{Helper.|}");
        Assert.Contains("Twice", labels);
        Assert.Contains("Label", labels);
        Assert.DoesNotContain("Count", labels);   // an instance member of the page
    }

    [Fact]
    public void ANamespaceQualifiedStaticType_DoesNotBreakCompletion()
    {
        // The type is found by its simple name only; a qualified one is not looked up and offers nothing, quietly.
        Assert.Empty(Complete("{App.Helper.|}"));
    }
}
