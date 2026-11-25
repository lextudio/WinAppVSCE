using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using WinUiXaml.LanguageServer.Lsp;
using WinUiXaml.Workspace;
using WinUiXaml.Xaml;
using Diagnostic = WinUiXaml.LanguageServer.Lsp.Diagnostic;

namespace WinUiXaml.LanguageServer;

internal static partial class XamlValidator
{
    /// <summary>An async lambda as an event expression (<c>{async (s, e) =&gt; ...}</c>): the XAML source generator refuses it (LWXSG0011).</summary>
    public const string ExpressionAsyncLambdaCode = "LWXSG0011";

    /// <summary>A simple name that the page and the DataContext (x:DataType) both have: the generator cannot choose (LWXSG0012).</summary>
    public const string ExpressionNameOnPageAndDataContextCode = "LWXSG0012";

    private static readonly Regex s_asyncLambda = new(@"^async\s+(\([^()]*\)|[A-Za-z_]\w*)\s*=>", RegexOptions.Compiled);
    private static readonly Regex s_simplePath = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$", RegexOptions.Compiled);

    /// <summary>
    /// The errors the XAML source generator reports for a C# expression value, shown while typing: the same ids as the build
    /// (LWXSG0011, LWXSG0012), so a squiggle and the build output name one problem.
    /// </summary>
    private static void ValidateExpressions(
        XamlElement element, XamlElement? ownerElement, INamedTypeSymbol? elementType, TextDocument doc, XamlTypeSystem typeSystem,
        List<Diagnostic> diagnostics, INamedTypeSymbol? pageClass)
    {
        foreach (var attribute in element.Attributes)
        {
            if (attribute.IsNamespaceDeclaration || attribute.Value is not { IsExpression: true } value
                || !XamlExpressionSyntax.TryLocate(value.Text, out var start, out var length))
            {
                continue;
            }

            var code = XamlExpressionSyntax.Decode(value.Text, start, length, out _).Trim();
            if (s_asyncLambda.IsMatch(code))
            {
                diagnostics.Add(Diag(doc, value.InnerSpan, SeverityError, ExpressionAsyncLambdaCode,
                    "An async lambda cannot be an event expression; use a regular method as the handler."));
                continue;
            }

            if (pageClass is null || elementType is null || !s_simplePath.IsMatch(code) || code.StartsWith("this.", System.StringComparison.Ordinal)
                || !IsDependencyProperty(elementType, attribute.Name.LocalName))
            {
                continue;
            }

            var first = code.Split('.')[0];
            var dataType = CompletionProvider.FindExpressionDataType(doc, value.InnerSpan.Start, typeSystem);
            if (CompletionProvider.FindExpressionMember(typeSystem, dataType, first, includeRootNonPublic: false, pageClass) is not null
                && CompletionProvider.FindExpressionMember(typeSystem, pageClass, first, includeRootNonPublic: true, pageClass) is not null)
            {
                diagnostics.Add(Diag(doc, value.InnerSpan, SeverityError, ExpressionNameOnPageAndDataContextCode,
                    $"'{first}' is a member of both the page ({pageClass.Name}) and the DataContext (x:DataType); write {{this.{first}}} for the page or {{.{first}}} for the DataContext."));
            }
        }
    }

    // A property that can take a binding: the type declares (or inherits) a static <Name>Property field.
    private static bool IsDependencyProperty(INamedTypeSymbol type, string propertyName)
    {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (t.GetMembers(propertyName + "Property").Any(m => m is IFieldSymbol { IsStatic: true }))
            {
                return true;
            }
        }

        return false;
    }
}
