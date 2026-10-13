using System.Globalization;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class NameplateTemplateDrawTests
{
    [Fact]
    public void AFullHeaderDrawsBarThenNameThenBadge()
    {
        var canvas = Canvas();

        Assert.True(Draw(canvas, HeaderTemplate(), FullHeader()));

        Assert.Equal(new[]
        {
            "fill 444.000 490.000 112.000 9.000 0.150 0.150 0.150 0.800 0.000",
            "fill 444.000 490.000 67.200 9.000 0.200 0.800 0.300 0.900 0.000",
            "stroke 443.000 489.000 114.000 11.000 0.000 0.000 0.000 1.000 0.000 1.000",
            "outlined \"Zombie\" 444.000 476.000 1.000 1.000 1.000 0.800 0.000 0.000 0.000 1.000 1.000 1.000",
            "fill 537.000 475.000 20.000 12.000 0.368 0.048 0.048 1.000 2.000",
            "stroke 537.000 475.000 20.000 12.000 0.000 0.000 0.000 1.000 2.000 1.000",
            "outlined \"12\" 541.000 476.000 1.000 0.800 0.200 0.600 0.000 0.000 0.000 1.000 1.000 1.000",
        }, canvas.Calls);
    }

    [Fact]
    public void TheBadgeWashesItsTintOverTheBase()
    {
        var badge = (NameplateBadgeComponent)HeaderTemplate().Components[2] with
        {
            BaseFill = Color.Black,
            TintAlpha = 0.5f,
        };
        var template = new NameplateTemplate(new NameplateComponent[] { badge });
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Level, "12", Color.White, new Color(1f, 0f, 0f, 0.2f));
        var canvas = Canvas();

        Assert.True(Draw(canvas, template, content));

        Assert.Equal("fill 490.000 488.000 20.000 12.000 0.500 0.000 0.000 1.000 2.000", canvas.Calls[0]);
    }

    [Fact]
    public void ABadgeWithoutATintUsesTheBaseFill()
    {
        var content = FullHeader();
        content.SetText(NameplateSlot.Level, "12", Color.White);
        var canvas = Canvas();

        Assert.True(Draw(canvas, HeaderTemplate(), content));

        Assert.Equal("fill 537.000 475.000 20.000 12.000 0.070 0.070 0.070 1.000 2.000", canvas.Calls[4]);
    }

    [Fact]
    public void ASlotBorderOverridesTheComponentBorder()
    {
        var content = FullHeader();
        content.SetBar(NameplateSlot.Health, 0.6f, Color.White, Color.Black, new Color(0.1f, 0.2f, 0.3f, 0.4f));
        var canvas = Canvas();

        Assert.True(Draw(canvas, HeaderTemplate(), content));

        Assert.Equal("stroke 443.000 489.000 114.000 11.000 0.100 0.200 0.300 0.400 0.000 1.000", canvas.Calls[2]);
    }

    [Fact]
    public void HalfOpacityHalvesEveryAlpha()
    {
        var content = FullHeader();
        content.Opacity = 0.5f;
        var canvas = Canvas();

        Assert.True(Draw(canvas, PanelAndShadowTemplate(), content));

        Assert.Equal(new[]
        {
            "fill 443.000 475.000 114.000 25.000 0.100 0.200 0.300 0.400 3.000",
            "stroke 443.000 475.000 114.000 25.000 0.400 0.500 0.600 0.300 3.000 2.000",
            "fill 444.000 490.000 112.000 9.000 0.150 0.150 0.150 0.400 0.000",
            "fill 444.000 490.000 67.200 9.000 0.200 0.800 0.300 0.450 0.000",
            "stroke 443.000 489.000 114.000 11.000 0.000 0.000 0.000 0.500 0.000 1.000",
            "text \"Zombie\" 446.000 479.000 0.200 0.300 0.400 0.300 1.000",
            "outlined \"Zombie\" 444.000 476.000 1.000 1.000 1.000 0.400 0.000 0.000 0.000 0.500 1.000 1.000",
            "fill 537.000 475.000 20.000 12.000 0.368 0.048 0.048 0.500 2.000",
            "stroke 537.000 475.000 20.000 12.000 0.000 0.000 0.000 0.500 2.000 1.000",
            "outlined \"12\" 541.000 476.000 1.000 0.800 0.200 0.300 0.000 0.000 0.000 0.500 1.000 1.000",
        }, canvas.Calls);
    }

    [Fact]
    public void ZeroOpacityDrawsAtAlphaZero()
    {
        var content = FullHeader();
        content.Opacity = 0f;
        var canvas = Canvas();

        Assert.True(Draw(canvas, PanelAndShadowTemplate(), content));

        Assert.Equal(10, canvas.Calls.Count);
        foreach (string call in canvas.Calls)
            AssertAllAlphas(call, 0f);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    public void OpacityOutsideTheRangeIsClamped(float opacity, float expectedAlpha)
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
        });
        var content = new NameplateContent { Opacity = opacity };
        content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        var canvas = Canvas();

        Assert.True(Draw(canvas, template, content));

        Assert.Single(canvas.Calls);
        AssertAllAlphas(canvas.Calls[0], expectedAlpha);
    }

    [Theory]
    [InlineData(-1f, "0.000")]
    [InlineData(2f, "112.000")]
    public void AFractionOutsideTheRangeIsClamped(float fraction, string expectedWidth)
    {
        var content = FullHeader();
        content.SetBar(NameplateSlot.Health, fraction, new Color(0.2f, 0.8f, 0.3f, 0.9f), Color.Black);
        var canvas = Canvas();

        Assert.True(Draw(canvas, HeaderTemplate(), content));

        Assert.Equal($"fill 444.000 490.000 {expectedWidth} 9.000 0.200 0.800 0.300 0.900 0.000", canvas.Calls[1]);
    }

    [Fact]
    public void AnEmptyTemplateDrawsNothingAndReturnsFalse()
    {
        var canvas = Canvas();

        Assert.False(Draw(canvas, HeaderTemplate(), new NameplateContent()));

        Assert.Empty(canvas.Calls);
    }

    [Fact]
    public void AMissingSlotSkipsItsComponent()
    {
        var content = FullHeader();
        content.SetText(NameplateSlot.Level, null, Color.White);
        var canvas = Canvas();

        Assert.True(Draw(canvas, HeaderTemplate(), content));

        Assert.Equal(4, canvas.Calls.Count);
        Assert.StartsWith("outlined \"Zombie\"", canvas.Calls[3]);
    }

    [Fact]
    public void PanelPaddingTranslatesTheResolvedComponents()
    {
        var template = HeaderTemplate() with { PadX = 5f, PadY = 3f, PanelFill = Color.Black };
        var canvas = Canvas();

        Assert.True(Draw(canvas, template, FullHeader()));

        Assert.Equal("fill 438.000 469.000 124.000 31.000 0.000 0.000 0.000 1.000 0.000", canvas.Calls[0]);
        Assert.Equal("fill 444.000 487.000 112.000 9.000 0.150 0.150 0.150 0.800 0.000", canvas.Calls[1]);
        Assert.Equal("outlined \"Zombie\" 444.000 473.000 1.000 1.000 1.000 0.800 0.000 0.000 0.000 1.000 1.000 1.000", canvas.Calls[4]);
    }

    [Theory]
    [InlineData(null, 1f, "489.000")]
    [InlineData(1f, 0f, "490.000")]
    public void TextWithoutAnOutlineColorOrThicknessUsesPlainText(float? outlineAlpha, float thickness, string expectedY)
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name)
            {
                OutlineColor = outlineAlpha.HasValue ? Color.Black.WithAlpha(outlineAlpha.Value) : null,
                OutlineThickness = thickness,
            },
        });
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        var canvas = Canvas();

        Assert.True(Draw(canvas, template, content));

        Assert.Single(canvas.Calls);
        Assert.Equal($"text \"Zombie\" 482.000 {expectedY} 1.000 1.000 1.000 1.000 1.000", canvas.Calls[0]);
    }

    [Fact]
    public void PlaceWithATemplateMatchesPlaceWithTheSameEdgeStyle()
    {
        var template = HeaderTemplate() with
        {
            EdgeBehavior = NameplateEdgeBehavior.Deflect,
            EdgeMargin = 4f,
            EdgeHysteresis = 10f,
        };
        var style = NameplateStyle.Default with
        {
            EdgeBehavior = NameplateEdgeBehavior.Deflect,
            EdgeMargin = 4f,
            EdgeHysteresis = 10f,
        };
        NameplatePlacementState templateState = default;
        NameplatePlacementState legacyState = default;
        var cases = new[]
        {
            (Anchor: new Vector2(500f, 5f), Rect: new Vector4(508f, 4f, 114f, 25f), Deflected: true),
            (Anchor: new Vector2(499f, 32f), Rect: new Vector4(507f, 19.5f, 114f, 25f), Deflected: true),
            (Anchor: new Vector2(900f, 33f), Rect: new Vector4(778f, 20.5f, 114f, 25f), Deflected: true),
            (Anchor: new Vector2(900f, 40f), Rect: new Vector4(843f, 15f, 114f, 25f), Deflected: false),
        };
        foreach (var test in cases)
        {
            Vector4 actual = NameplatePlacement.Place(test.Anchor, new Vector2(114f, 25f), 1000, 1000,
                template, ref templateState);
            Vector4 legacy = NameplatePlacement.Place(test.Anchor, new Vector2(114f, 25f), 1000, 1000,
                style, ref legacyState);

            Assert.Equal(test.Rect, actual);
            Assert.Equal(legacy, actual);
            Assert.Equal(test.Deflected, templateState.IsDeflected);
            Assert.Equal(legacyState.Side, templateState.Side);
        }
    }

    private static RecordingNameplateCanvas Canvas() => new(new FixedGlyphFont(6f, 10f));

    private static bool Draw(RecordingNameplateCanvas canvas, NameplateTemplate template, NameplateContent content)
    {
        NameplatePlacementState state = default;
        return NameplateRenderer.DrawTemplateAt(ref canvas, new Vector2(500f, 500f), template, content,
            1000, 1000, ref state);
    }

    private static NameplateTemplate HeaderTemplate() => new(new NameplateComponent[]
    {
        NameplateTemplateLayoutTests.HeaderTemplate.Components[0],
        (NameplateTextComponent)NameplateTemplateLayoutTests.HeaderTemplate.Components[1] with { OutlineColor = Color.Black },
        (NameplateBadgeComponent)NameplateTemplateLayoutTests.HeaderTemplate.Components[2] with { OutlineColor = Color.Black },
    });

    private static NameplateTemplate PanelAndShadowTemplate()
    {
        var header = HeaderTemplate();
        return new NameplateTemplate(new NameplateComponent[]
        {
            header.Components[0],
            (NameplateTextComponent)header.Components[1] with
            {
                ShadowColor = new Color(0.2f, 0.3f, 0.4f, 0.6f),
                ShadowOffset = new Vector2(2f, 3f),
            },
            header.Components[2],
        })
        {
            PanelFill = new Color(0.1f, 0.2f, 0.3f, 0.8f),
            PanelBorder = new Color(0.4f, 0.5f, 0.6f, 0.6f),
            PanelBorderThickness = 2f,
            CornerRadius = 3f,
        };
    }

    private static NameplateContent FullHeader()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Zombie", new Color(1f, 1f, 1f, 0.8f));
        content.SetText(NameplateSlot.Level, "12", new Color(1f, 0.8f, 0.2f, 0.6f), new Color(1f, 0f, 0f, 0.2f));
        content.SetBar(NameplateSlot.Health, 0.6f, new Color(0.2f, 0.8f, 0.3f, 0.9f), new Color(0.15f, 0.15f, 0.15f, 0.8f));
        return content;
    }

    private static void AssertAllAlphas(string call, float expected)
    {
        string[] tokens = call.Split(' ');
        int alphaIndex = tokens[0] is "fill" or "stroke" ? 8 : 7;
        Assert.Equal(expected, float.Parse(tokens[alphaIndex], CultureInfo.InvariantCulture));
        if (tokens[0] == "outlined")
            Assert.Equal(expected, float.Parse(tokens[11], CultureInfo.InvariantCulture));
    }
}
