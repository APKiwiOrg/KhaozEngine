using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

public static partial class NameplateRenderer
{
    /// <summary>Draws a component template at a caller-resolved panel rectangle in framebuffer pixels.</summary>
    /// <remarks>
    /// Does not project, distance-cull, clamp, deflect or resize components. Keep the panel dimensions from
    /// <see cref="NameplateTemplateLayout.Measure"/> and use a framebuffer-space sprite batch pass.
    /// </remarks>
    /// <param name="batch">An in-progress sprite batch to draw into.</param>
    /// <param name="font">The font used to measure and draw text.</param>
    /// <param name="white">A white texture for solid panel, badge and bar fills.</param>
    /// <param name="panelRect">Panel X, Y, width and height in framebuffer pixels.</param>
    /// <param name="template">The component layout and panel styling.</param>
    /// <param name="content">The current component values and whole-plate opacity.</param>
    /// <param name="viewportWidth">Positive framebuffer width in pixels.</param>
    /// <param name="viewportHeight">Positive framebuffer height in pixels.</param>
    /// <returns>False for missing resources, invalid viewport dimensions or panel geometry, or empty content.</returns>
    public static bool DrawAtRect(
        SpriteBatch batch, SpriteFont font, Texture2D white, Vector4 panelRect,
        NameplateTemplate template, NameplateContent content, int viewportWidth, int viewportHeight)
    {
        if (batch is null || font is null || white is null || template is null || content is null) return false;

        var canvas = new SpriteBatchNameplateCanvas(batch, font, white);
        return DrawTemplateAtRect(ref canvas, panelRect, template, content, viewportWidth, viewportHeight);
    }

    /// <summary>Draws a component template at a caller-resolved panel rectangle already in design space.</summary>
    /// <remarks>
    /// Uses the sprite batch pass's existing design transform without remapping the supplied rectangle.
    /// Does not project, distance-cull, clamp, deflect or resize components. Keep the panel dimensions from
    /// <see cref="NameplateTemplateLayout.Measure"/> in the same design units.
    /// </remarks>
    /// <param name="batch">An in-progress sprite batch drawing through the design viewport.</param>
    /// <param name="font">The font used to measure and draw text.</param>
    /// <param name="white">A white texture for solid panel, badge and bar fills.</param>
    /// <param name="panelRect">Panel X, Y, width and height already in design units.</param>
    /// <param name="template">The component layout and panel styling.</param>
    /// <param name="content">The current component values and whole-plate opacity.</param>
    /// <param name="designViewport">The design viewport used by the sprite batch pass.</param>
    /// <returns>False for missing resources, invalid viewport dimensions or panel geometry, or empty content.</returns>
    public static bool DrawAtRect(
        SpriteBatch batch, SpriteFont font, Texture2D white, Vector4 panelRect,
        NameplateTemplate template, NameplateContent content, IDesignViewport designViewport)
    {
        if (batch is null || font is null || white is null || template is null || content is null || designViewport is null) return false;

        var canvas = new SpriteBatchNameplateCanvas(batch, font, white);
        return DrawTemplateAtRect(ref canvas, panelRect, template, content, designViewport);
    }

    internal static bool DrawTemplateAtRect<TCanvas>(
        ref TCanvas canvas, Vector4 panelRect, NameplateTemplate template, NameplateContent content,
        IDesignViewport designViewport)
        where TCanvas : INameplateCanvas =>
        designViewport is not null && DrawTemplateAtRect(ref canvas, panelRect, template, content,
            designViewport.Width, designViewport.Height);

    internal static bool DrawTemplateAtRect<TCanvas>(
        ref TCanvas canvas, Vector4 panelRect, NameplateTemplate template, NameplateContent content,
        int viewportWidth, int viewportHeight)
        where TCanvas : INameplateCanvas
    {
        if (template is null || content is null || viewportWidth <= 0 || viewportHeight <= 0 ||
            !float.IsFinite(panelRect.X) || !float.IsFinite(panelRect.Y) ||
            !float.IsFinite(panelRect.Z) || !float.IsFinite(panelRect.W) ||
            panelRect.Z <= 0f || panelRect.W <= 0f ||
            !float.IsFinite(panelRect.X + panelRect.Z) || !float.IsFinite(panelRect.Y + panelRect.W)) return false;

        NameplateResolution resolution = NameplateResolution.Shared;
        if (!NameplateTemplateLayout.Resolve(canvas.Font, template, content, resolution)) return false;

        DrawResolvedTemplateAtRect(ref canvas, panelRect, template, content, NameplateTemplateColors.Of(template), resolution);
        return true;
    }

    private static void DrawResolvedTemplateAtRect<TCanvas>(
        ref TCanvas canvas, Vector4 panelRect, NameplateTemplate template, NameplateContent content,
        in NameplateTemplateColors colors, NameplateResolution resolution)
        where TCanvas : INameplateCanvas
    {
        float opacity = float.IsFinite(content.Opacity) ? Math.Clamp(content.Opacity, 0f, 1f) : 1f;
        if (colors.PanelFill.A > 0f)
            canvas.FillRounded(panelRect, WithOpacity(colors.PanelFill, opacity), template.CornerRadius);
        if (template.PanelBorderThickness > 0f && colors.PanelBorder.A > 0f)
            canvas.StrokeRounded(panelRect, WithOpacity(colors.PanelBorder, opacity), template.CornerRadius, template.PanelBorderThickness);

        Vector2 screenOffset = new(panelRect.X + template.PadX - resolution.Bounds.X,
            panelRect.Y + template.PadY - resolution.Bounds.Y);
        for (int i = 0; i < template.Components.Count; i++)
        {
            ref NameplateResolvedComponent resolved = ref resolution[i];
            if (!resolved.Drawn) continue;

            Vector4 rect = new(resolved.Rect.X + screenOffset.X, resolved.Rect.Y + screenOffset.Y,
                resolved.Rect.Width, resolved.Rect.Height);
            switch (template.Components[i])
            {
                case NameplateTextComponent text when content.TryGetText(text.Slot, out var value):
                    DrawTemplateText(ref canvas, rect, resolved.Text, text, colors.ShadowFor(text), value, opacity);
                    break;
                case NameplateBadgeComponent badge when content.TryGetText(badge.Slot, out var value):
                    DrawTemplateBadge(ref canvas, rect, resolved.Text, badge, value, opacity);
                    break;
                case NameplateBarComponent bar when content.TryGetBar(bar.Slot, out var value):
                    DrawTemplateBar(ref canvas, rect, bar, value, opacity);
                    break;
            }
        }
    }
}
