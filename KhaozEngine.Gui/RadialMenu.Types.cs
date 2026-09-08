using System.Numerics;
using KhaozEngine.App;

namespace KhaozEngine.Gui
{
    public readonly record struct RadialMenuEntry(
        LocalizedText Content,
        long Tag,
        string? IconId = null,
        bool Enabled = true,
        LocalizedText Detail = default,
        long InitialChoiceTag = 0);

    public readonly record struct RadialMenuChoice(
        LocalizedText Content,
        long Tag,
        bool Enabled = true);

    public readonly record struct RadialMenuSelection(long EntryTag, long ChoiceTag);

    public readonly record struct RadialMenuChoiceChange(long EntryTag, long ChoiceTag);

    public readonly record struct RadialMenuMetrics(
        float InnerRadius,
        float OuterRadius,
        float WedgeGap,
        float IconSize,
        float LabelScale,
        float DetailGap,
        float FooterGap,
        Vector2 FooterButtonSize,
        float Margin,
        float BorderThickness,
        Vector2 ShadowOffset,
        float SheenSpeed)
    {
        public static RadialMenuMetrics Default { get; } = new(
            54f,
            148f,
            0.035f,
            30f,
            0.82f,
            10f,
            10f,
            new Vector2(56f, 30f),
            8f,
            1.5f,
            new Vector2(0f, 5f),
            0.12f);
    }
}
