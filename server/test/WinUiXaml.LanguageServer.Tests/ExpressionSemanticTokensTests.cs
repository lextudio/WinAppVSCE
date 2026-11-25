using System.Collections.Generic;
using System.Linq;
using WinUiXaml.LanguageServer;
using Xunit;

namespace WinUiXaml.LanguageServer.Tests;

/// <summary>
/// C# expressions in attribute values (<c>{Count + 1}</c>, the syntax of .NET MAUI that the LibreWPF XAML source generator
/// follows) are coloured as C# when the document is read with expressions on, and stay markup extensions otherwise.
/// </summary>
public class ExpressionSemanticTokensTests
{
    private const string Head = "<Window x:Class=\"App.MainWindow\"><TextBlock ";

    private static List<(string Type, string Covered)> Tokens(string attributes, bool expressions = true)
    {
        var text = Head + attributes + " /></Window>";
        var doc = new TextDocument("file:///a.xaml", text, null, implicitNamespaces: true, csharpExpressions: expressions);
        var data = XamlSemanticTokens.Compute(doc).Data;
        var result = new List<(string, string)>();
        int line = 0, ch = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            if (data[i] == 0) ch += data[i + 1]; else { line += data[i]; ch = data[i + 1]; }
            var start = doc.OffsetAt(new WinUiXaml.LanguageServer.Lsp.Position { Line = line, Character = ch });
            result.Add((XamlSemanticTokens.TokenTypes[data[i + 3]], text.Substring(start, data[i + 2])));
        }

        return result;
    }

    private static List<(string Type, string Covered)> Inside(string attributes, bool expressions = true) =>
        Tokens(attributes, expressions).Where(t => t.Type is "variable" or "keyword" or "string" or "number" or "operator" or "method" || t.Covered is "Count" or "Name").ToList();

    [Fact]
    public void ABareExpression_IsColouredAsCSharp()
    {
        var tokens = Inside("Text=\"{Count + 1}\"");
        Assert.Contains(("variable", "Count"), tokens);
        Assert.Contains(("operator", "+"), tokens);
        Assert.Contains(("number", "1"), tokens);
    }

    [Fact]
    public void AnExplicitExpression_SkipsTheEquals_AndColoursSingleQuotedStrings()
    {
        var tokens = Inside("Text=\"{= Name ?? 'none'}\"");
        Assert.Contains(("variable", "Name"), tokens);
        Assert.Contains(("operator", "??"), tokens);
        Assert.Contains(("string", "'none'"), tokens);
    }

    [Fact]
    public void OperatorWords_AreOperators_AndMembersAreProperties()
    {
        var tokens = Inside("Text=\"{User.Count GT 0 AND IsOn}\"");
        Assert.Contains(("property", "Count"), tokens);
        Assert.Contains(("operator", "GT"), tokens);
        Assert.Contains(("operator", "AND"), tokens);
        Assert.Contains(("variable", "IsOn"), tokens);
    }

    [Fact]
    public void AnInterpolatedString_ColoursItsHoles_AndKeepsTheFormatAString()
    {
        var tokens = Inside("Text=\"{$'Total: {Price:C2}'}\"");
        Assert.Contains(("variable", "Price"), tokens);
        Assert.Contains(("string", ":C2"), tokens);
        Assert.Contains(("string", "$'Total: {"), tokens);
    }

    [Fact]
    public void XmlEntities_AreDecoded_SoOperatorsKeepTheirDocumentPosition()
    {
        var tokens = Inside("Text=\"{Count &gt; 0 &amp;&amp; IsOn}\"");
        Assert.Contains(("operator", "&gt;"), tokens);
        Assert.Contains(("operator", "&amp;&amp;"), tokens);
        Assert.Contains(("variable", "IsOn"), tokens);
    }

    [Fact]
    public void AMethodCall_AndALambda_AreRecognised()
    {
        var tokens = Inside("Click=\"{(s, e) => Save(1)}\"");
        Assert.Contains(("method", "Save"), tokens);
        Assert.Contains(("operator", "=>"), tokens);
        Assert.Contains(("number", "1"), tokens);
    }

    [Theory]
    [InlineData("Text=\"{Binding Name}\"")]
    [InlineData("Text=\"{StaticResource Key}\"")]
    [InlineData("Text=\"{x:Static Foo.Bar}\"")]
    public void AMarkupExtension_IsNotColouredAsAnExpression(string attributes)
    {
        var tokens = Tokens(attributes);
        Assert.DoesNotContain(tokens, t => t.Type == "variable");
        Assert.Contains(tokens, t => t.Type == "macro");
    }

    [Fact]
    public void WithExpressionsOff_TheValueIsAMarkupExtensionAsBefore()
    {
        var tokens = Tokens("Text=\"{Count}\"", expressions: false);
        Assert.Contains(("macro", "Count"), tokens);
        Assert.DoesNotContain(tokens, t => t.Type == "variable");
    }
}
