using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>
/// Panel and text shadow colours a draw uses in place of the template's own, so one cached legacy template
/// serves every tint and fade of the same geometry.
/// </summary>
internal readonly struct NameplateTemplateColors
{
    private NameplateTemplateColors(Color panelFill, Color panelBorder, Color? textShadow, bool overridesTextShadow)
    {
        PanelFill = panelFill;
        PanelBorder = panelBorder;
        TextShadow = textShadow;
        OverridesTextShadow = overridesTextShadow;
    }

    internal Color PanelFill { get; }
    internal Color PanelBorder { get; }
    internal Color? TextShadow { get; }
    internal bool OverridesTextShadow { get; }

    /// <summary>The template's own colours, leaving each text component's shadow colour in place.</summary>
    internal static NameplateTemplateColors Of(NameplateTemplate template) =>
        new(template.PanelFill, template.PanelBorder, null, false);

    /// <summary>A legacy style's colours. Its title shadow replaces the shadow colour of every text component.</summary>
    internal static NameplateTemplateColors Of(in NameplateStyle style) =>
        new(style.PanelFill, style.PanelBorder, style.TitleShadow, true);

    internal Color? ShadowFor(NameplateTextComponent component) =>
        OverridesTextShadow ? TextShadow : component.ShadowColor;
}
