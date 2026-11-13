using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace Surface;

/// <summary>One authored element of a rendered frame: its authored-tree path, identity and bounds in frame DIPs.</summary>
/// <summary>A Grid track: offset from the Grid's origin and rendered size.</summary>
public sealed record FrameTrack(double Offset, double Size);

/// <summary>
/// Measured layout facts of one element, enough for drop targets and snapping without native mode: <c>Margin</c> and the attached
/// <c>Canvas</c>/<c>Grid</c> values as laid out, and for a Grid or panel its tracks / orientation. A field is null when it does not apply.
/// </summary>
public sealed record FrameLayout(
    double MarginLeft, double MarginTop, double MarginRight, double MarginBottom,
    string? ParentKind,                       // "Grid", "Stack", "Canvas", "Wrap", "Dock" or null for the root / unknown
    int? Row, int? Column, int? RowSpan, int? ColumnSpan,
    double? CanvasLeft, double? CanvasTop, int? ZIndex,
    string? Orientation,                      // "Vertical"/"Horizontal" for StackPanel and WrapPanel
    IReadOnlyList<FrameTrack>? Rows, IReadOnlyList<FrameTrack>? Columns);   // when this element is a Grid

public sealed record FrameTreeNode(string Path, string Type, string? Name, double X, double Y, double W, double H, FrameLayout? Layout = null);

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
        Walk(root, root, frame, "0", null, nodes);
        return nodes;
    }

    static void Walk(FrameworkElement element, FrameworkElement root, FrameworkElement frame, string path, FrameworkElement? parent, List<FrameTreeNode> nodes)
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
            bounds.X, bounds.Y, bounds.Width, bounds.Height, LayoutOf(element, parent)));

        int index = 0;
        foreach (var child in AuthoredChildren(element, root))
        {
            Walk(child, root, frame, path + "." + index, element, nodes);
            index++;
        }
    }

    /// <summary>The element at <paramref name="path"/> ("0" the root, "0.1" its second authored child), or null.</summary>
    public static FrameworkElement? Resolve(FrameworkElement root, string path)
    {
        var parts = path.Split('.');
        if (parts.Length == 0 || parts[0] != "0")
        {
            return null;
        }
        FrameworkElement current = root;
        for (int i = 1; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int index) || index < 0)
            {
                return null;
            }
            int k = 0;
            FrameworkElement? next = null;
            foreach (var child in AuthoredChildren(current, root))
            {
                if (k++ == index)
                {
                    next = child;
                    break;
                }
            }
            if (next is null)
            {
                return null;
            }
            current = next;
        }
        return current;
    }

    static string? KindOf(FrameworkElement? parent) => parent switch
    {
        null => null,
        Grid => "Grid",
        Canvas => "Canvas",
        StackPanel => "Stack",
        RelativePanel => "Relative",
        _ => parent.GetType().Name switch { "WrapPanel" or "WrapGrid" => "Wrap", "DockPanel" => "Dock", "UniformGrid" => "Grid", _ => null },
    };

    static FrameLayout? LayoutOf(FrameworkElement element, FrameworkElement? parent)
    {
        try
        {
            var margin = element.Margin;
            string? kind = KindOf(parent);
            int? row = null, col = null, rowSpan = null, colSpan = null;
            double? left = null, top = null;
            int? z = null;
            if (parent is Grid)
            {
                row = Grid.GetRow(element); col = Grid.GetColumn(element);
                rowSpan = Grid.GetRowSpan(element); colSpan = Grid.GetColumnSpan(element);
            }
            if (parent is Canvas)
            {
                left = Canvas.GetLeft(element); top = Canvas.GetTop(element);
            }
            if (parent != null)
            {
                z = Canvas.GetZIndex(element);
            }
            string? orientation = element switch
            {
                StackPanel sp => sp.Orientation.ToString(),
                _ => null,
            };
            IReadOnlyList<FrameTrack>? rows = null, cols = null;
            if (element is Grid grid)
            {
                rows = Tracks(grid.RowDefinitions.Count, i => grid.RowDefinitions[i].ActualHeight);
                cols = Tracks(grid.ColumnDefinitions.Count, i => grid.ColumnDefinitions[i].ActualWidth);
            }
            return new FrameLayout(margin.Left, margin.Top, margin.Right, margin.Bottom, kind, row, col, rowSpan, colSpan, left, top, z,
                orientation, rows, cols);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A Grid with no authored definitions has one implicit track filling it; an empty list says "none declared".
    static List<FrameTrack> Tracks(int count, Func<int, double> size)
    {
        var tracks = new List<FrameTrack>(count);
        double offset = 0;
        for (int i = 0; i < count; i++)
        {
            double s = size(i);
            tracks.Add(new FrameTrack(offset, s));
            offset += s;
        }
        return tracks;
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
