using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>Greedily places screen-space overlays in deterministic priority order.</summary>
internal static class WorldMapOverlayLayout
{
    static readonly Comparison<(ScreenIcon Icon, int Index)> IconOrder = CompareIcons;
    static readonly Comparison<(ScreenLabel Label, int Index)> LabelOrder = CompareLabels;

    internal static OverlayPlacement Place(IReadOnlyList<ScreenIcon> icons, IReadOnlyList<ScreenLabel> labels,
        Rect viewport, float pixelsPerMetre, Vector2 pointer, WorldMapOverlayBuffers buffers)
    {
        WorldMapHover? previousHover = buffers.Placement.Hover;
        buffers.Clear();
        for (int i = 0; i < icons.Count; i++) buffers.SortedIcons.Add((icons[i], i));
        buffers.SortedIcons.Sort(IconOrder);
        foreach (var candidate in buffers.SortedIcons)
        {
            ScreenIcon icon = candidate.Icon;
            Rect rect = Bounds(icon.Position, icon.Size);
            if (!Inside(rect, viewport)) continue;
            bool blocked = false;
            for (int i = 0; i < buffers.PlacedIcons.Count; i++)
            {
                ScreenIcon accepted = buffers.PlacedIcons[i];
                if ((icon.Key == accepted.Key && Vector2.Distance(icon.Position, accepted.Position) <= icon.MergeRadius)
                    || Overlaps(rect, buffers.IconRects[i]))
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked) continue;
            buffers.PlacedIcons.Add(icon);
            buffers.IconRects.Add(rect);
        }

        for (int i = 0; i < labels.Count; i++) buffers.SortedLabels.Add((labels[i], i));
        buffers.SortedLabels.Sort(LabelOrder);
        foreach (var candidate in buffers.SortedLabels)
        {
            ScreenLabel label = candidate.Label;
            if (label.Text is null || pixelsPerMetre < label.Tier.MinPixelsPerMetre
                || pixelsPerMetre >= label.Tier.MaxPixelsPerMetre) continue;
            Rect rect = Bounds(label.Position, label.Size);
            if (!Inside(rect, viewport)) continue;
            bool blocked = false;
            foreach (Rect accepted in buffers.LabelRects)
            {
                if (Overlaps(rect, accepted))
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked) continue;
            buffers.PlacedLabels.Add(label);
            buffers.LabelRects.Add(rect);
        }

        WorldMapHover? hover = null;
        for (int i = buffers.PlacedIcons.Count - 1; i >= 0; i--)
        {
            ScreenIcon icon = buffers.PlacedIcons[i];
            if (!buffers.IconRects[i].Contains(pointer)) continue;
            hover = Hover(previousHover, WorldMapHoverKind.Icon, icon.Key, icon.MapPosition);
            break;
        }
        if (hover is null)
        {
            for (int i = buffers.PlacedLabels.Count - 1; i >= 0; i--)
            {
                ScreenLabel label = buffers.PlacedLabels[i];
                if (!buffers.LabelRects[i].Contains(pointer)) continue;
                hover = Hover(previousHover, WorldMapHoverKind.Label, label.MarkerName, label.MapPosition);
                break;
            }
        }
        buffers.Placement.Hover = hover;
        return buffers.Placement;
    }

    static int CompareIcons((ScreenIcon Icon, int Index) left, (ScreenIcon Icon, int Index) right)
    {
        int order = right.Icon.Priority.CompareTo(left.Icon.Priority);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Icon.Key, right.Icon.Key);
        if (order != 0) return order;
        order = left.Icon.Position.X.CompareTo(right.Icon.Position.X);
        if (order != 0) return order;
        order = left.Icon.Position.Y.CompareTo(right.Icon.Position.Y);
        return order != 0 ? order : left.Index.CompareTo(right.Index);
    }

    static int CompareLabels((ScreenLabel Label, int Index) left, (ScreenLabel Label, int Index) right)
    {
        int order = right.Label.Tier.Rank.CompareTo(left.Label.Tier.Rank);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Label.MarkerName, right.Label.MarkerName);
        if (order != 0) return order;
        order = left.Label.Position.X.CompareTo(right.Label.Position.X);
        if (order != 0) return order;
        order = left.Label.Position.Y.CompareTo(right.Label.Position.Y);
        return order != 0 ? order : left.Index.CompareTo(right.Index);
    }

    static WorldMapHover Hover(WorldMapHover? previous, WorldMapHoverKind kind, string key, Vector2 mapPosition) =>
        previous is not null && previous.Kind == kind && previous.Key == key && previous.MapPosition == mapPosition
            ? previous : new WorldMapHover(kind, key, mapPosition);

    static Rect Bounds(Vector2 center, Vector2 size) =>
        new(center.X - size.X / 2f, center.Y - size.Y / 2f, size.X, size.Y);

    static bool Inside(Rect rect, Rect viewport) =>
        rect.X >= viewport.X && rect.Y >= viewport.Y && rect.Right <= viewport.Right && rect.Bottom <= viewport.Bottom;

    static bool Overlaps(Rect left, Rect right) =>
        left.X < right.Right && left.Right > right.X && left.Y < right.Bottom && left.Bottom > right.Y;
}
