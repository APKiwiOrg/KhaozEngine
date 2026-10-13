using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>Adapts the title-and-bars API to reusable component templates.</summary>
internal static class NameplateLegacyTemplate
{
    private const int Capacity = 16;

    private static readonly NameplateSlot[] BarSlots =
    [
        new("legacy.bar.0"), new("legacy.bar.1"), new("legacy.bar.2"), new("legacy.bar.3"),
    ];

    private static readonly string[] BarIds = ["bar0", "bar1", "bar2", "bar3"];

    [ThreadStatic]
    private static CacheEntry[]? _cache;
    [ThreadStatic]
    private static int _count;
    [ThreadStatic]
    private static int _next;

    /// <summary>
    /// The template for a style's geometry. Styles that differ only in panel or title shadow colour share one
    /// template, so the caller draws with <see cref="NameplateTemplateColors.Of(in NameplateStyle)"/>.
    /// </summary>
    internal static NameplateTemplate For(in NameplateStyle style, int barCount)
    {
        if (barCount > BarSlots.Length)
            return Build(style, barCount);

        NameplateStyle key = GeometryOf(style);
        _cache ??= new CacheEntry[Capacity];
        for (int i = 0; i < _count; i++)
            if (_cache[i].Geometry.Equals(key))
                return _cache[i].Template;

        NameplateTemplate template = Build(style, BarSlots.Length);
        _cache[_next] = new CacheEntry(key, template);
        _next = (_next + 1) % _cache.Length;
        if (_count < _cache.Length) _count++;
        return template;
    }

    internal static void Fill(NameplateContent content, in Nameplate plate)
    {
        content.Clear();
        content.SetText(NameplateSlot.Name, plate.Title, plate.TitleColor);
        if (plate.Bars is not { } bars) return;

        for (int i = 0; i < bars.Count; i++)
        {
            NameplateBar bar = bars[i];
            content.SetBar(BarSlot(i), bar.Fraction, bar.Fill, bar.Track);
        }
    }

    private static NameplateTemplate Build(in NameplateStyle style, int barCount)
    {
        var components = new NameplateComponent[barCount + 1];
        components[0] = new NameplateTextComponent("title", NameplateSlot.Name)
        {
            FontScale = style.FontScale,
            ShadowColor = style.TitleShadow,
            ShadowOffset = style.TitleShadowOffset,
        };
        for (int i = 0; i < barCount; i++)
        {
            components[i + 1] = new NameplateBarComponent(BarId(i), BarSlot(i))
            {
                Width = 0f,
                Height = style.BarHeight,
                CornerRadius = style.BarCornerRadius,
                BorderThickness = 0f,
                SkipEmptyFill = true,
                Attach = new NameplateAttach(AttachPoint.Top, components[i].Id, AttachPoint.Bottom,
                    new Vector2(0f, style.BarSpacing)),
            };
        }

        return new NameplateTemplate(components)
        {
            PanelFill = style.PanelFill,
            PanelBorder = style.PanelBorder,
            PanelBorderThickness = style.PanelBorderThickness,
            CornerRadius = style.CornerRadius,
            PadX = style.PadX,
            PadY = style.PadY,
            MinContentWidth = style.MinBarWidth,
            MaxContentWidth = style.MaxWidth > 0f ? MathF.Max(0f, style.MaxWidth - 2f * style.PadX) : 0f,
            EdgeBehavior = style.EdgeBehavior,
            EdgeMargin = style.EdgeMargin,
            EdgeHysteresis = style.EdgeHysteresis,
        };
    }

    // Every field but the colours, keeping whether a title shadow is set.
    private static NameplateStyle GeometryOf(in NameplateStyle style) => style with
    {
        PanelFill = default,
        PanelBorder = default,
        TitleShadow = style.TitleShadow.HasValue ? default(Color) : null,
    };

    private static NameplateSlot BarSlot(int index) =>
        index < BarSlots.Length ? BarSlots[index] : new NameplateSlot($"legacy.bar.{index}");

    private static string BarId(int index) => index < BarIds.Length ? BarIds[index] : $"bar{index}";

    private readonly record struct CacheEntry(NameplateStyle Geometry, NameplateTemplate Template);
}
