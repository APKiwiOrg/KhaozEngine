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
    /// A framebuffer pass does not snap, so the plate draws exactly where the rectangle says.
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
    /// In a point-space UI pass (<see cref="IDesignViewport.SnapsToDevicePixels"/>) the panel origin lands on a
    /// whole device pixel and every component is placed from its offset to that origin with a whole device pixel
    /// size and border, so a plate that follows a moving anchor keeps its shape and a steady border. Decoration
    /// drawn around the panel lines up with it when it starts from the same snapped origin.
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
        // The origin snaps first, and every component is placed from its constant offset to that origin, so the
        // whole plate moves a device pixel at a time as its anchor drifts and keeps its shape on every row.
        NameplateDeviceGrid grid = NameplateDeviceGrid.At(ref canvas, panelRect);
        Vector4 panel = grid.Panel(panelRect);
        if (colors.PanelFill.A > 0f)
            canvas.FillRounded(panel, WithOpacity(colors.PanelFill, opacity), template.CornerRadius);
        if (template.PanelBorderThickness > 0f && colors.PanelBorder.A > 0f)
            canvas.StrokeRounded(panel, WithOpacity(colors.PanelBorder, opacity), template.CornerRadius,
                grid.SnapThickness(template.PanelBorderThickness));

        Vector2 local = new(template.PadX - resolution.Bounds.X, template.PadY - resolution.Bounds.Y);
        Vector2 screenOffset = new(panelRect.X + template.PadX - resolution.Bounds.X,
            panelRect.Y + template.PadY - resolution.Bounds.Y);
        for (int i = 0; i < template.Components.Count; i++)
        {
            ref NameplateResolvedComponent resolved = ref resolution[i];
            if (!resolved.Drawn) continue;

            Vector4 rect = grid.Place(resolved.Rect, local, screenOffset);
            switch (template.Components[i])
            {
                case NameplateTextComponent text when content.TryGetText(text.Slot, out var value):
                    DrawTemplateText(ref canvas, rect, resolved.Text, text, colors.ShadowFor(text), value, opacity);
                    break;
                case NameplateBadgeComponent badge when content.TryGetText(badge.Slot, out var value):
                    DrawTemplateBadge(ref canvas, grid, rect, resolved.Text, badge, value, opacity);
                    break;
                case NameplateBarComponent bar when content.TryGetBar(bar.Slot, out var value):
                    DrawTemplateBar(ref canvas, grid, rect, bar, value, opacity);
                    break;
            }
        }
    }
}
