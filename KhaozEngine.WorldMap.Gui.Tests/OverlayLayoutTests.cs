using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

/// <summary>Proves deterministic screen-space decluttering, zoom gates and hover precedence.</summary>
public sealed class OverlayLayoutTests
{
    static readonly Rect Viewport = new(100f, 50f, 400f, 300f);
    static readonly WorldMapLabelTier Tier = new("town", 1, 2f, 4f, 1f);

    /// <summary>The lower-priority same-key icon merges at the inclusive pixel-radius boundary.</summary>
    [Fact]
    public void SameKey_WithinMergeRadius_CollapsesToOne()
    {
        ScreenIcon first = Icon("bank", new Vector2(200f, 100f), 2, 24f);
        ScreenIcon second = Icon("bank", new Vector2(224f, 100f), 1, 24f);

        var placed = Place([second, first]);

        Assert.Equal(first, Assert.Single(placed.Icons));
    }

    /// <summary>Same-key icons beyond the merge radius remain distinct at any map zoom.</summary>
    [Fact]
    public void SameKey_BeyondMergeRadius_BothShown()
    {
        ScreenIcon first = Icon("bank", new Vector2(200f, 100f), 2, 24f);
        ScreenIcon second = Icon("bank", new Vector2(225f, 100f), 1, 24f);

        var placed = WorldMapOverlayLayout.Place([second, first], [], Viewport, 8f, Vector2.Zero);

        Assert.Equal(new[] { first, second }, placed.Icons);
    }

    /// <summary>Overlapping rectangles select the higher-priority icon even with different keys.</summary>
    [Fact]
    public void DifferentKeys_Overlapping_HigherPriorityWins()
    {
        ScreenIcon low = Icon("anvil", new Vector2(200f, 100f), 1);
        ScreenIcon high = Icon("bank", new Vector2(206f, 100f), 2);

        var placed = Place([low, high]);

        Assert.Equal(high, Assert.Single(placed.Icons));
    }

    /// <summary>A partly clipped high-priority icon cannot suppress a fully visible icon.</summary>
    [Fact]
    public void Icon_PartlyOutsideViewport_IsDropped()
    {
        ScreenIcon clipped = Icon("bank", new Vector2(101f, 100f), 10);
        ScreenIcon visible = Icon("bank", new Vector2(104f, 100f), 1);

        var placed = Place([clipped, visible]);

        Assert.Equal(visible, Assert.Single(placed.Icons));
    }

    /// <summary>A tier excludes zoom below its minimum and at its exclusive maximum.</summary>
    [Theory]
    [InlineData(1.999f)]
    [InlineData(4f)]
    public void Label_OutsideTierZoomRange_IsDropped(float zoom)
    {
        var placed = WorldMapOverlayLayout.Place([], [Label("town", new Vector2(200f, 100f))],
            Viewport, zoom, Vector2.Zero);

        Assert.Empty(placed.Labels);
    }

    /// <summary>The inclusive minimum zoom admits a label centred on its anchor.</summary>
    [Fact]
    public void Label_AtMinimumZoom_IsShown()
    {
        ScreenLabel label = Label("town", new Vector2(200f, 100f));

        Assert.Equal(label, Assert.Single(Place([], [label]).Labels));
    }

    /// <summary>A centred label leaving the viewport is dropped.</summary>
    [Fact]
    public void Label_PartlyOutsideViewport_IsDropped()
    {
        Assert.Empty(Place([], [Label("town", new Vector2(105f, 100f))]).Labels);
    }

    /// <summary>Higher tier rank wins an overlap regardless of source order or marker name.</summary>
    [Fact]
    public void Labels_Colliding_HigherRankWins()
    {
        ScreenLabel low = Label("a-village", new Vector2(200f, 100f));
        ScreenLabel high = Label("z-city", new Vector2(210f, 100f)) with { Tier = Tier with { Rank = 2 } };

        var placed = Place([], [low, high]);

        Assert.Equal(high, Assert.Single(placed.Labels));
    }

    /// <summary>Icons win over label text, label-only hits work and empty-space hits clear hover.</summary>
    [Fact]
    public void Hover_PrefersIcon_ThenLabel_ElseNull()
    {
        ScreenIcon icon = Icon("bank", new Vector2(200f, 100f));
        ScreenLabel label = Label("town", new Vector2(200f, 100f));

        var iconHit = Place([icon], [label], new Vector2(200f, 100f));
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Icon, "bank", new Vector2(1f, 2f)), iconHit.Hover);
        var labelHit = Place([icon], [label], new Vector2(190f, 100f));
        Assert.Equal(new WorldMapHover(WorldMapHoverKind.Label, "town", new Vector2(3f, 4f)), labelHit.Hover);
        Assert.Null(Place([icon], [label], new Vector2(300f, 200f)).Hover);
    }

    /// <summary>Edge-touching rectangles coexist and half-open hit testing selects the later icon.</summary>
    [Fact]
    public void TouchingIcons_DoNotCollide_HoverUsesHalfOpenBounds()
    {
        ScreenIcon first = Icon("a", new Vector2(200f, 100f));
        ScreenIcon second = Icon("b", new Vector2(208f, 100f));

        var placed = Place([second, first], pointer: new Vector2(204f, 100f));

        Assert.Equal(2, placed.Icons.Count);
        Assert.Equal("b", placed.Hover!.Key);
    }

    /// <summary>Ordinal keys, marker names and anchor positions break ties independently of input order.</summary>
    [Fact]
    public void EqualPriorities_PlaceDeterministically()
    {
        ScreenIcon[] icons = [Icon("z", new Vector2(200f, 100f)), Icon("a", new Vector2(200f, 100f)),
            Icon("a", new Vector2(300f, 100f)), Icon("a", new Vector2(300f, 150f))];
        ScreenLabel[] labels = [Label("z", new Vector2(200f, 200f)), Label("a", new Vector2(200f, 200f)),
            Label("a", new Vector2(300f, 200f))];

        var forward = Place(icons, labels);
        var shuffled = Place([icons[3], icons[1], icons[0], icons[2]], labels.Reverse().ToArray());

        Assert.Equal(new[] { new Vector2(200f, 100f), new Vector2(300f, 100f), new Vector2(300f, 150f) },
            forward.Icons.Select(icon => icon.Position));
        Assert.All(forward.Icons, icon => Assert.Equal("a", icon.Key));
        Assert.Equal(new[] { "a", "a" }, forward.Labels.Select(label => label.MarkerName));
        Assert.Equal(forward.Icons, shuffled.Icons);
        Assert.Equal(forward.Labels, shuffled.Labels);
    }

    static ScreenIcon Icon(string key, Vector2 position, int priority = 1, float radius = 0f) =>
        new(key, position, new Vector2(8f, 8f), priority, radius, new Vector2(1f, 2f));

    static ScreenLabel Label(string name, Vector2 position) =>
        new(name, "Resolved town", position, new Vector2(30f, 10f), Tier, new Vector2(3f, 4f));

    static OverlayPlacement Place(ScreenIcon[] icons, ScreenLabel[]? labels = null, Vector2 pointer = default) =>
        WorldMapOverlayLayout.Place(icons, labels ?? [], Viewport, 2f, pointer);
}
