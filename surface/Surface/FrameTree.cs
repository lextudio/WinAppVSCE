using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace Surface;

/// <summary>One authored element of a rendered frame: its authored-tree path, identity and bounds in frame DIPs.</summary>
public sealed record FrameTreeNode(string Path, string Type, string? Name, double X, double Y, double W, double H);

/// <summary>
/// The authored element tree of a frame-mode render, with each element's bounds in the frame's DIP space. Paths use the same scheme as
/// the native design surface (<c>PreviewSurface.PathForElement</c>): child indices through the authored content property, "0" for the page
/// root, "0.1" for its second authored child, so a client can map frame clicks and selection to the source without native mode.
/// The walk mirrors <c>PreviewSurface.AuthoredChildren</c>: a control's template internals, a nested UserControl's private content and a
/// cleaner placeholder's label are not authored and are skipped.
/// </summary>
internal static class FrameTree
{
    const int MaxNodes = 2000;
    static readonly Dictionary<Type, string?> s_contentProps = new();

    public static IReadOnlyList<FrameTreeNode> Describe(FrameworkElement root, FrameworkElement frame)
    {
        var nodes = new List<FrameTreeNode>();
        Walk(root, root, frame, "0", nodes);
        return nodes;
    }

    static void Walk(FrameworkElement element, FrameworkElement root, FrameworkElement frame, string path, List<FrameTreeNode> nodes)
    {
        if (nodes.Count >= MaxNodes)
        {
            return;
        }

        Rect bounds;
        try
        {
            bounds = element.TransformToVisual(frame).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (Exception)
        {
            bounds = new Rect(0, 0, 0, 0);   // not in the visual tree (collapsed, unrealized): still listed so paths stay aligned
        }
        nodes.Add(new FrameTreeNode(path, element.GetType().Name, string.IsNullOrEmpty(element.Name) ? null : element.Name,
            bounds.X, bounds.Y, bounds.Width, bounds.Height));

        int index = 0;
        foreach (var child in AuthoredChildren(element, root))
        {
            Walk(child, root, frame, path + "." + index, nodes);
            index++;
        }
    }

    static IEnumerable<FrameworkElement> AuthoredChildren(DependencyObject obj, FrameworkElement root)
    {
        string? cp = ContentPropertyName(obj.GetType());
        if (cp == null)
        {
            yield break;
        }
        // A nested UserControl's Content is its private compiled template; the page root's Content is authored.
        if (cp == "Content" && obj is UserControl && !ReferenceEquals(obj, root))
        {
            yield break;
        }
        if (obj is Border { Tag: string tag } && tag == XamlCleaner.PlaceholderTag)
        {
            yield break;
        }

        object? value;
        try
        {
            value = obj.GetType().GetProperty(cp)?.GetValue(obj);
        }
        catch (Exception)
        {
            yield break;
        }

        if (value is FrameworkElement single)
        {
            yield return single;
            yield break;
        }
        if (value is IEnumerable seq && value is not string)
        {
            foreach (var item in seq)
            {
                if (item is FrameworkElement fe && !(fe.Tag is string t && t == SampleChartFiller.OverlayTag))
                {
                    yield return fe;
                }
            }
        }
    }

    // Same rule as PreviewSurface.ContentPropertyName: match the attribute by name (WinUI 2 controls carry the legacy one).
    static string? ContentPropertyName(Type t)
    {
        if (s_contentProps.TryGetValue(t, out var cached))
        {
            return cached;
        }
        string? name = null;
        for (Type? cur = t; cur != null && name == null; cur = cur.BaseType)
        {
            foreach (var data in cur.GetCustomAttributesData())
            {
                if (data.AttributeType.Name != nameof(ContentPropertyAttribute))
                {
                    continue;
                }
                foreach (var arg in data.NamedArguments)
                {
                    if (arg.MemberName == nameof(ContentPropertyAttribute.Name))
                    {
                        name = arg.TypedValue.Value as string;
                    }
                }
                break;
            }
        }
        s_contentProps[t] = name;
        return name;
    }
}
