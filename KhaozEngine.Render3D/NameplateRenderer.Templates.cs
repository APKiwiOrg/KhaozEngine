using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

public static partial class NameplateRenderer
{
    /// <summary>Projects and draws a component template in framebuffer pixels, with content opacity.</summary>
    /// <remarks>Keep one placement state per entity across frames for deflection hysteresis.</remarks>
    public static bool Draw(
        SpriteBatch batch, SpriteFont font, Texture2D white,
        IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
        NameplateTemplate template, NameplateContent content,
        int viewportWidth, int viewportHeight,
        ref NameplatePlacementState placementState,
        float maxDistance = 0f, Vector3? cullFrom = null)
    {
        if (batch is null || font is null || white is null || camera is null || template is null || content is null) return false;

        Vector3 cullOrigin = cullFrom ?? camera.Eye;
        if (ShouldCull(worldPos, cullOrigin, maxDistance)) return false;
        if (!NameplateAnchorProjection.Project(camera, worldPos, offset, viewportWidth, viewportHeight, out Vector2 pixel)) return false;

        var canvas = new SpriteBatchNameplateCanvas(batch, font, white);
        return DrawTemplateAt(ref canvas, pixel, template, content, viewportWidth, viewportHeight, ref placementState);
    }

    /// <summary>Projects and draws a component template in the design viewport used by the HUD pass.</summary>
    /// <remarks>Keep one placement state per entity across frames for deflection hysteresis.</remarks>
    public static bool Draw(
        SpriteBatch batch, SpriteFont font, Texture2D white,
        IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
        NameplateTemplate template, NameplateContent content,
        IDesignViewport designViewport,
        ref NameplatePlacementState placementState,
        float maxDistance = 0f, Vector3? cullFrom = null)
    {
        if (batch is null || font is null || white is null || camera is null || designViewport is null || template is null || content is null) return false;

        Vector3 cullOrigin = cullFrom ?? camera.Eye;
        if (ShouldCull(worldPos, cullOrigin, maxDistance)) return false;
        if (!NameplateAnchorProjection.Project(camera, worldPos, offset, designViewport, out Vector2 pixel)) return false;

        var canvas = new SpriteBatchNameplateCanvas(batch, font, white);
        return DrawTemplateAt(ref canvas, pixel, template, content, designViewport.Width, designViewport.Height, ref placementState);
    }

    internal static bool DrawTemplateAt<TCanvas>(
        ref TCanvas canvas, Vector2 pixel, NameplateTemplate template, NameplateContent content,
        int viewportWidth, int viewportHeight, ref NameplatePlacementState placementState)
        where TCanvas : INameplateCanvas =>
        DrawTemplateAt(ref canvas, pixel, template, content, NameplateTemplateColors.Of(template),
            viewportWidth, viewportHeight, ref placementState);

    // The colours replace the template's panel colours, and its text shadows when they override them.
    internal static bool DrawTemplateAt<TCanvas>(
        ref TCanvas canvas, Vector2 pixel, NameplateTemplate template, NameplateContent content,
        in NameplateTemplateColors colors,
        int viewportWidth, int viewportHeight, ref NameplatePlacementState placementState)
        where TCanvas : INameplateCanvas
    {
        NameplateResolution resolution = NameplateResolution.Shared;
        if (!NameplateTemplateLayout.Resolve(canvas.Font, template, content, resolution)) return false;

        Vector2 size = new(resolution.Bounds.Width + 2f * template.PadX,
            resolution.Bounds.Height + 2f * template.PadY);
        if (size.X <= 0f || size.Y <= 0f) return false;

        Vector4 panelRect = NameplatePlacement.Place(pixel, size, viewportWidth, viewportHeight, template, ref placementState);
        DrawResolvedTemplateAtRect(ref canvas, panelRect, template, content, colors, resolution);
        return true;
    }

    private static void DrawTemplateText<TCanvas>(ref TCanvas canvas, Vector4 rect, string text,
        NameplateTextComponent component, Color? shadowColor, NameplateTextValue value, float opacity)
        where TCanvas : INameplateCanvas
    {
        Vector2 position = new(rect.X + component.OutlineThickness, rect.Y + component.OutlineThickness);
        if (shadowColor is Color shadow)
            canvas.Text(text, position + component.ShadowOffset, WithOpacity(shadow, opacity), component.FontScale);
        if (component.OutlineThickness > 0f && component.OutlineColor is Color outline)
            canvas.OutlinedText(text, position, WithOpacity(value.Color, opacity), WithOpacity(outline, opacity),
                component.OutlineThickness, component.FontScale);
        else
            canvas.Text(text, position, WithOpacity(value.Color, opacity), component.FontScale);
    }

    private static void DrawTemplateBadge<TCanvas>(ref TCanvas canvas, Vector4 rect, string text,
        NameplateBadgeComponent component, NameplateTextValue value, float opacity)
        where TCanvas : INameplateCanvas
    {
        Color wash = value.Tint is Color tint
            ? Color.Lerp(component.BaseFill, tint.WithAlpha(component.BaseFill.A), component.TintAlpha)
            : component.BaseFill;
        canvas.FillRounded(rect, WithOpacity(wash, opacity), component.CornerRadius);
        if (component.BorderThickness > 0f)
            canvas.StrokeRounded(rect, WithOpacity(component.BorderColor, opacity), component.CornerRadius, component.BorderThickness);

        Vector2 glyph = canvas.Font.Measure(text) * component.FontScale;
        Vector2 position = new(rect.X + (rect.Z - glyph.X) * 0.5f, rect.Y + (rect.W - glyph.Y) * 0.5f);
        if (component.OutlineThickness > 0f && component.OutlineColor is Color outline)
            canvas.OutlinedText(text, position, WithOpacity(value.Color, opacity), WithOpacity(outline, opacity),
                component.OutlineThickness, component.FontScale);
        else
            canvas.Text(text, position, WithOpacity(value.Color, opacity), component.FontScale);
    }

    private static void DrawTemplateBar<TCanvas>(ref TCanvas canvas, Vector4 rect,
        NameplateBarComponent component, NameplateBarValue value, float opacity)
        where TCanvas : INameplateCanvas
    {
        Vector4 inner = new(rect.X + component.BorderThickness, rect.Y + component.BorderThickness,
            rect.Z - 2f * component.BorderThickness, rect.W - 2f * component.BorderThickness);
        canvas.FillRounded(inner, WithOpacity(value.Track, opacity), component.CornerRadius);
        float fraction = Math.Clamp(value.Fraction, 0f, 1f);
        if (!float.IsNaN(value.Fraction) && (!component.SkipEmptyFill || fraction > 0f))
        {
            Vector4 fill = inner with { Z = inner.Z * fraction };
            canvas.FillRounded(fill, WithOpacity(value.Fill, opacity), component.CornerRadius);
        }
        if (component.BorderThickness > 0f)
            canvas.StrokeRounded(rect, WithOpacity(value.Border ?? component.BorderColor, opacity),
                component.CornerRadius, component.BorderThickness);
    }

    private static Color WithOpacity(Color color, float opacity) => color.WithAlpha(color.A * opacity);
}
