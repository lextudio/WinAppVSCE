using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using WinUiXaml.LanguageServer.Lsp;
using WinUiXaml.Workspace;
using WinUiXaml.Xaml;

namespace WinUiXaml.LanguageServer;

/// <summary>
/// Completion inside a C# expression attribute value (<c>{Count + 1}</c>, <c>{= User.Na</c>, the syntax of .NET MAUI that the
/// LibreWPF XAML source generator follows). A name is looked for where the generator looks for it: the DataContext type named by
/// the nearest <c>x:DataType</c> (a binding), then the page's <c>x:Class</c> (local access). <c>this.</c> asks for the page only and a
/// leading <c>.</c> for the DataContext only; a dotted path is followed property by property.
/// </summary>
internal static partial class CompletionProvider
{
    private static readonly string[] s_expressionKeywords = { "this", "new", "true", "false", "null" };

    /// <summary>The items for the caret inside an expression value, or null when the caret is not inside one (the usual completion then applies).</summary>
    public static CompletionList? ProvideExpression(TextDocument doc, int offset, XamlTypeSystem typeSystem, INamedTypeSymbol? pageClass)
    {
        XamlAttributeValue? value = null;
        for (var node = doc.Parsed.FindNode(Math.Max(0, offset - 1)); node != null; node = node.Parent)
        {
            if (node is XamlAttributeValue candidate)
            {
                value = candidate;
                break;
            }
        }

        if (value is not { IsExpression: true } || offset < value.InnerSpan.Start || offset > value.InnerSpan.End
            || !XamlExpressionSyntax.TryLocate(value.Text, out var start, out var length))
        {
            return null;
        }

        int relative = offset - value.InnerSpan.Start;
        if (relative < start || relative > start + length)
        {
            // On the braces, outside the code: not an expression position, so the ordinary attribute-value completion applies.
            return null;
        }

        var code = XamlExpressionSyntax.Decode(value.Text, start, length, out var rawOffsets);
        int caret = 0;
        while (caret < code.Length && rawOffsets[caret + 1] <= relative)
        {
            caret++;
        }

        // Inside a string literal there is nothing to complete (a hole of an interpolated string lexes as code, not as a string).
        foreach (var token in XamlExpressionLexer.Lex(code))
        {
            if (token.Kind == XamlExpressionLexer.Kind.String && token.Start < caret && caret < token.Start + token.Length)
            {
                return new CompletionList();
            }
        }

        int chainStart = caret;
        while (chainStart > 0 && (char.IsLetterOrDigit(code[chainStart - 1]) || code[chainStart - 1] == '_' || code[chainStart - 1] == '.'))
        {
            chainStart--;
        }

        var chain = code.Substring(chainStart, caret - chainStart);
        if (chain.Length > 0 && char.IsDigit(chain[0]))
        {
            return new CompletionList();
        }

        // A name that follows a call or an index (a().B, x[0].B) has a type this does not follow.
        if (chainStart > 0 && (code[chainStart - 1] == ')' || code[chainStart - 1] == ']'))
        {
            return new CompletionList();
        }

        int lastDot = chain.LastIndexOf('.');
        var partial = lastDot < 0 ? chain : chain.Substring(lastDot + 1);
        var prefix = lastDot < 0 ? string.Empty : chain.Substring(0, lastDot);
        bool explicitDataContext = prefix.Length == 0 && lastDot == 0;
        int replaceFrom = value.InnerSpan.Start + rawOffsets[caret - partial.Length];
        var replaceRange = doc.RangeOf(new TextSpan(replaceFrom, offset));

        var dataType = FindExpressionDataType(doc, offset, typeSystem);
        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (prefix.Length == 0)
        {
            AddExpressionMembers(items, seen, typeSystem, dataType, "DataContext", partial, replaceRange, includeRootNonPublic: false, accessWithin: pageClass, sort: "0");
            if (!explicitDataContext)
            {
                AddExpressionMembers(items, seen, typeSystem, pageClass, "page", partial, replaceRange, includeRootNonPublic: true, accessWithin: pageClass, sort: "1");
                foreach (var keyword in s_expressionKeywords)
                {
                    if (StartsWith(keyword, partial))
                    {
                        items.Add(new CompletionItem { Label = keyword, Kind = CompletionItemKind.Keyword, TextEdit = new TextEdit { Range = replaceRange, NewText = keyword }, SortText = "9" + keyword });
                    }
                }
            }

            return Finish(items);
        }

        var (current, fromPage, isStaticType) = WalkExpressionPrefix(doc, offset, prefix, explicitDataContext, dataType, pageClass, typeSystem);
        if (current is null)
        {
            return new CompletionList();
        }

        var members = isStaticType
            ? typeSystem.GetBindableStaticMembers(current, pageClass)
            : typeSystem.GetBindableMembers(current, includeRootNonPublic: fromPage && prefix.Trim() == "this", pageClass);
        foreach (var member in members)
        {
            if (!StartsWith(member.Name, partial) || IsBindCompletionNoise(member) || !seen.Add(member.Name))
            {
                continue;
            }

            items.Add(ExpressionMemberItem(member, null, replaceRange, "0"));
        }

        return Finish(items);
    }

    private static void AddExpressionMembers(
        List<CompletionItem> items, HashSet<string> seen, XamlTypeSystem typeSystem, ITypeSymbol? type, string origin,
        string partial, Lsp.Range replaceRange, bool includeRootNonPublic, ISymbol? accessWithin, string sort)
    {
        if (type is null)
        {
            return;
        }

        foreach (var member in typeSystem.GetBindableMembers(type, includeRootNonPublic, accessWithin))
        {
            if (!StartsWith(member.Name, partial) || IsBindCompletionNoise(member) || !seen.Add(member.Name))
            {
                continue;
            }

            items.Add(ExpressionMemberItem(member, origin, replaceRange, sort));
        }
    }

    private static CompletionItem ExpressionMemberItem(ISymbol member, string? origin, Lsp.Range replaceRange, string sort) => new()
    {
        Label = member.Name,
        Kind = BindMemberKind(member),
        Documentation = CompletionDoc(member),
        Detail = origin is null ? DescribeBindMember(member) : origin + " " + DescribeBindMember(member),
        TextEdit = new TextEdit { Range = replaceRange, NewText = member.Name },
        FilterText = member.Name,
        SortText = sort + (member is IMethodSymbol ? "1" : "0") + member.Name,
    };

    /// <summary>The type of the nearest <c>x:DataType</c> above the caret: what a bare name binds against.</summary>
    internal static ITypeSymbol? FindExpressionDataType(TextDocument doc, int offset, XamlTypeSystem typeSystem)
    {
        for (var node = doc.Parsed.FindNode(Math.Max(0, offset - 1)); node != null; node = node.Parent)
        {
            if (node is not XamlElement element)
            {
                continue;
            }

            var attribute = element.Attributes.FirstOrDefault(a =>
                !a.IsNamespaceDeclaration && XamlSemanticFacts.IsXamlDirective(a, "DataType", element.NamespaceScope));
            if (attribute?.Value?.Text is { } text && text.Trim() is { Length: > 0 } typeName)
            {
                return XamlSemanticFacts.ResolveTypeName(typeName, element.NamespaceScope, typeSystem);
            }
        }

        return null;
    }

    // A type named in an expression is looked up the way C# does: the page's own namespace (and its parents), the project's global
    // usings, System (the SDK's implicit usings), and last the XAML namespaces of the document.
    private static ITypeSymbol? ResolveExpressionTypeName(TextDocument doc, int offset, string name, XamlTypeSystem typeSystem, INamedTypeSymbol? pageClass = null)
    {
        var candidates = new List<string>();
        for (var ns = pageClass?.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            candidates.Add(ns.ToDisplayString());
        }

        candidates.AddRange(typeSystem.GlobalUsingNamespaces);
        candidates.Add("System");
        foreach (var ns in candidates)
        {
            if (typeSystem.ResolveMetadataType(ns + "." + name) is { } found)
            {
                return found;
            }
        }

        var scope = EffectiveScope(doc.Parsed.FindNode(Math.Max(0, offset - 1)), doc.Parsed);
        return XamlSemanticFacts.ResolveTypeName(name, scope, typeSystem);
    }

    /// <summary>
    /// The type a dotted prefix (<c>User.Home</c>) leads to, how the generator would read its first name (the page when it came from
    /// <c>this</c> or the page's members, a static type when it names one), or null when a step does not resolve.
    /// </summary>
    private static (ITypeSymbol? Type, bool FromPage, bool IsStaticType) WalkExpressionPrefix(
        TextDocument doc, int offset, string prefix, bool explicitDataContext, ITypeSymbol? dataType, INamedTypeSymbol? pageClass, XamlTypeSystem typeSystem)
    {
        ITypeSymbol? current = null;
        bool atRoot = true;
        bool fromPage = false;
        bool isStaticType = false;
        foreach (var raw in prefix.Split('.'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
            {
                return (null, false, false);
            }

            ITypeSymbol? resolved;
            if (atRoot && segment == "this" && !explicitDataContext)
            {
                resolved = pageClass;
                fromPage = true;
            }
            else if (atRoot)
            {
                resolved = dataType is null ? null : ResolveBindSegmentType(typeSystem, dataType, segment, atRoot: false, pageClass);
                if (resolved is null && pageClass is not null)
                {
                    resolved = ResolveBindSegmentType(typeSystem, pageClass, segment, atRoot: true, pageClass);
                    fromPage = resolved is not null;
                }

                // A type name (Math.Max, DateTime.Now): its static members follow.
                if (resolved is null)
                {
                    resolved = ResolveExpressionTypeName(doc, offset, segment, typeSystem, pageClass);
                    isStaticType = resolved is not null;
                }
            }
            else
            {
                resolved = ResolveBindSegmentType(typeSystem, current!, segment, atRoot: false, pageClass);
                isStaticType = false;
            }

            if (resolved is null)
            {
                return (null, false, false);
            }

            current = resolved;
            atRoot = false;
        }

        return (current, fromPage, isStaticType);
    }

    /// <summary>
    /// The member under the caret in an expression value (for hover and go to definition), with the span of its name; null when the
    /// caret is not on a name that resolves.
    /// </summary>
    internal static (ISymbol Symbol, TextSpan Span)? ResolveExpressionMemberAt(TextDocument doc, int offset, XamlTypeSystem typeSystem, INamedTypeSymbol? pageClass)
    {
        XamlAttributeValue? value = null;
        for (var node = doc.Parsed.FindNode(Math.Max(0, offset - 1)); node != null; node = node.Parent)
        {
            if (node is XamlAttributeValue candidate)
            {
                value = candidate;
                break;
            }
        }

        if (value is not { IsExpression: true } || offset < value.InnerSpan.Start || offset > value.InnerSpan.End
            || !XamlExpressionSyntax.TryLocate(value.Text, out var start, out var length))
        {
            return null;
        }

        int relative = offset - value.InnerSpan.Start;
        var code = XamlExpressionSyntax.Decode(value.Text, start, length, out var rawOffsets);
        int caret = 0;
        while (caret < code.Length && rawOffsets[caret + 1] <= relative)
        {
            caret++;
        }

        foreach (var token in XamlExpressionLexer.Lex(code))
        {
            if (token.Kind == XamlExpressionLexer.Kind.String && token.Start <= caret && caret < token.Start + token.Length)
            {
                return null;
            }
        }

        static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        int chainStart = Math.Min(caret, code.Length);
        while (chainStart > 0 && (IsNameChar(code[chainStart - 1]) || code[chainStart - 1] == '.'))
        {
            chainStart--;
        }

        int wordEnd = Math.Min(caret, code.Length);
        while (wordEnd < code.Length && IsNameChar(code[wordEnd]))
        {
            wordEnd++;
        }

        if (wordEnd <= chainStart || (chainStart > 0 && (code[chainStart - 1] == ')' || code[chainStart - 1] == ']')))
        {
            return null;
        }

        var chain = code.Substring(chainStart, wordEnd - chainStart);
        int lastDot = chain.LastIndexOf('.');
        var name = lastDot < 0 ? chain : chain.Substring(lastDot + 1);
        var prefix = lastDot < 0 ? string.Empty : chain.Substring(0, lastDot);
        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            return null;
        }

        bool explicitDataContext = lastDot == 0;
        var dataType = FindExpressionDataType(doc, offset, typeSystem);
        ISymbol? symbol;
        if (prefix.Length == 0)
        {
            symbol = FindExpressionMember(typeSystem, dataType, name, includeRootNonPublic: false, pageClass);
            if (symbol is null && !explicitDataContext)
            {
                symbol = FindExpressionMember(typeSystem, pageClass, name, includeRootNonPublic: true, pageClass);
            }
        }
        else
        {
            var (current, fromPage, isStaticType) = WalkExpressionPrefix(doc, offset, prefix, explicitDataContext, dataType, pageClass, typeSystem);
            if (current is null)
            {
                return null;
            }

            symbol = isStaticType
                ? typeSystem.GetBindableStaticMembers(current, pageClass).FirstOrDefault(m => m.Name == name)
                : FindExpressionMember(typeSystem, current, name, includeRootNonPublic: fromPage && prefix.Trim() == "this", pageClass);
        }

        if (symbol is null)
        {
            return null;
        }

        int from = value.InnerSpan.Start + rawOffsets[wordEnd - name.Length];
        int to = value.InnerSpan.Start + rawOffsets[wordEnd];
        return (symbol, TextSpan.FromBounds(from, to));
    }

    internal static ISymbol? FindExpressionMember(XamlTypeSystem typeSystem, ITypeSymbol? type, string name, bool includeRootNonPublic, ISymbol? accessWithin) =>
        type is null ? null : typeSystem.GetBindableMembers(type, includeRootNonPublic, accessWithin).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
}
