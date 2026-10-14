using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

/// <summary>Headless measurement and ordered attachment of nameplate components from their current content.</summary>
public static class NameplateTemplateLayout
{
    /// <summary>Measures the drawn component bounds plus panel padding, or zero when no component draws.</summary>
    public static Vector2 Measure(ITextMeasurer font, NameplateTemplate template, NameplateContent content)
    {
        NameplateResolution resolution = NameplateResolution.Shared;
        if (!Resolve(font, template, content, resolution))
            return Vector2.Zero;

        return new Vector2(resolution.Bounds.Width + 2f * template.PadX,
            resolution.Bounds.Height + 2f * template.PadY);
    }

    internal static bool Resolve(ITextMeasurer font, NameplateTemplate template, NameplateContent content,
        NameplateResolution resolution)
    {
        int count = template.Components.Count;
        resolution.Prepare(count);

        // Content width comes from natural text and badge widths before any ellipsis.
        float naturalWidth = 0f;
        for (int i = 0; i < count; i++)
        {
            ref NameplateResolvedComponent resolved = ref resolution[i];
            switch (template.Components[i])
            {
                case NameplateTextComponent text when content.TryGetText(text.Slot, out var value):
                    {
                        Vector2 size = TextOutline.Measure(font, value.Text, text.FontScale, text.OutlineThickness);
                        resolved.Drawn = true;
                        resolved.Text = value.Text;
                        resolved.Rect = new Rect(0f, 0f, size.X, size.Y);
                        naturalWidth = MathF.Max(naturalWidth, size.X);
                        break;
                    }
                case NameplateBadgeComponent badge when content.TryGetText(badge.Slot, out var value):
                    {
                        Vector2 glyph = TextOutline.Measure(font, value.Text, badge.FontScale, badge.OutlineThickness);
                        float width = MathF.Max(badge.MinWidth, glyph.X + 2f * badge.PadX);
                        resolved.Drawn = true;
                        resolved.Text = value.Text;
                        resolved.Rect = new Rect(0f, 0f, width, glyph.Y + 2f * badge.PadY);
                        naturalWidth = MathF.Max(naturalWidth, width);
                        break;
                    }
                case NameplateBarComponent bar:
                    resolved.Drawn = content.TryGetBar(bar.Slot, out _);
                    break;
            }
        }

        resolution.ContentWidth = MathF.Min(MathF.Max(naturalWidth, template.MinContentWidth),
            template.MaxContentWidth > 0f ? template.MaxContentWidth : float.PositiveInfinity);

        for (int i = 0; i < count; i++)
        {
            ref NameplateResolvedComponent resolved = ref resolution[i];
            if (!resolved.Drawn || template.Components[i] is not NameplateTextComponent text)
                continue;

            float componentCap = text.MaxWidth - 2f * text.OutlineThickness;
            float glyphCap = componentCap > 0f ? componentCap : float.PositiveInfinity;
            if (template.MaxContentWidth > 0f)
                glyphCap = MathF.Min(glyphCap, template.MaxContentWidth - 2f * text.OutlineThickness);

            if (resolved.Rect.Width - 2f * text.OutlineThickness > glyphCap)
            {
                resolved.Text = NameplateLayout.Ellipsize(font, resolved.Text, glyphCap, text.FontScale);
                Vector2 size = TextOutline.Measure(font, resolved.Text, text.FontScale, text.OutlineThickness);
                resolved.Rect = new Rect(0f, 0f, size.X, size.Y);
            }
        }

        for (int i = 0; i < count; i++)
        {
            ref NameplateResolvedComponent resolved = ref resolution[i];
            if (!resolved.Drawn || template.Components[i] is not NameplateBarComponent bar)
                continue;

            float width = bar.Width > 0f ? bar.Width : resolution.ContentWidth;
            resolved.Rect = new Rect(0f, 0f, width + 2f * bar.BorderThickness,
                bar.Height + 2f * bar.BorderThickness);
        }

        for (int i = 1; i < count; i++)
        {
            NameplateAttach attach = template.Components[i].Attach;
            int targetIndex = 0;
            if (attach.Target is not null)
            {
                for (int j = 0; j < i; j++)
                {
                    if (template.Components[j].Id == attach.Target)
                    {
                        targetIndex = j;
                        break;
                    }
                }
            }

            // Skipped components have zero size but keep their attach point for later dependents.
            ref NameplateResolvedComponent resolved = ref resolution[i];
            resolved.Rect = Attach.Resolve(resolution[targetIndex].Rect, attach.TargetPoint,
                resolved.Rect.Width, resolved.Rect.Height, attach.Self, attach.Offset.X, attach.Offset.Y);
        }

        bool drawn = false;
        Rect bounds = default;
        for (int i = 0; i < count; i++)
        {
            if (!resolution[i].Drawn)
                continue;

            bounds = drawn ? Attach.Union(bounds, resolution[i].Rect) : resolution[i].Rect;
            drawn = true;
        }

        if (drawn)
        {
            float width = MathF.Max(bounds.Width, resolution.ContentWidth);
            resolution.Bounds = new Rect(bounds.X - (width - bounds.Width) * 0.5f, bounds.Y, width, bounds.Height);
        }

        return drawn;
    }
}
