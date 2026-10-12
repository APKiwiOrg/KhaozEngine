using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>Greedily places screen-space overlays in deterministic priority order.</summary>
internal static class WorldMapOverlayLayout
{
    internal static OverlayPlacement Place(IReadOnlyList<ScreenIcon> icons, IReadOnlyList<ScreenLabel> labels,
        Rect viewport, float pixelsPerMetre, Vector2 pointer)
    {
        var placedIcons = new List<ScreenIcon>();
        foreach (ScreenIcon icon in icons.OrderByDescending(icon => icon.Priority)
            .ThenBy(icon => icon.Key, StringComparer.Ordinal).ThenBy(icon => icon.Position.X).ThenBy(icon => icon.Position.Y))
        {
            Rect rect = Bounds(icon.Position, icon.Size);
            if (!Inside(rect, viewport)) continue;
            bool blocked = false;
            foreach (ScreenIcon accepted in placedIcons)
            {
                if ((icon.Key == accepted.Key && Vector2.Distance(icon.Position, accepted.Position) <= icon.MergeRadius)
                    || Overlaps(rect, Bounds(accepted.Position, accepted.Size)))
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked) placedIcons.Add(icon);
        }

        var placedLabels = new List<ScreenLabel>();
        foreach (ScreenLabel label in labels.OrderByDescending(label => label.Tier.Rank)
            .ThenBy(label => label.MarkerName, StringComparer.Ordinal)
            .ThenBy(label => label.Position.X).ThenBy(label => label.Position.Y))
        {
            if (label.Text is null || pixelsPerMetre < label.Tier.MinPixelsPerMetre
                || pixelsPerMetre >= label.Tier.MaxPixelsPerMetre) continue;
            Rect rect = Bounds(label.Position, label.Size);
            if (!Inside(rect, viewport)) continue;
            bool blocked = false;
            foreach (ScreenLabel accepted in placedLabels)
            {
                if (Overlaps(rect, Bounds(accepted.Position, accepted.Size)))
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked) placedLabels.Add(label);
        }

        WorldMapHover? hover = null;
        for (int i = placedIcons.Count - 1; i >= 0; i--)
        {
            ScreenIcon icon = placedIcons[i];
            if (!Bounds(icon.Position, icon.Size).Contains(pointer)) continue;
            hover = new WorldMapHover(WorldMapHoverKind.Icon, icon.Key, icon.MapPosition);
            break;
        }
        if (hover is null)
        {
            for (int i = placedLabels.Count - 1; i >= 0; i--)
            {
                ScreenLabel label = placedLabels[i];
                if (!Bounds(label.Position, label.Size).Contains(pointer)) continue;
                hover = new WorldMapHover(WorldMapHoverKind.Label, label.MarkerName, label.MapPosition);
                break;
            }
        }
        return new OverlayPlacement(placedIcons.AsReadOnly(), placedLabels.AsReadOnly(), hover);
    }

    static Rect Bounds(Vector2 center, Vector2 size) =>
        new(center.X - size.X / 2f, center.Y - size.Y / 2f, size.X, size.Y);

    static bool Inside(Rect rect, Rect viewport) =>
        rect.X >= viewport.X && rect.Y >= viewport.Y && rect.Right <= viewport.Right && rect.Bottom <= viewport.Bottom;

    static bool Overlaps(Rect left, Rect right) =>
        left.X < right.Right && left.Right > right.X && left.Y < right.Bottom && left.Bottom > right.Y;
}
