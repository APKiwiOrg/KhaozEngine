using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class NameplateTemplateLayoutTests
{
    private static readonly FixedGlyphFont Font = new(6f, 10f);

    internal static NameplateTemplate HeaderTemplate = new(new NameplateComponent[]
    {
        new NameplateBarComponent("health", NameplateSlot.Health)
        {
            Width = 112f,
            Height = 9f,
            BorderThickness = 1f,
        },
        new NameplateTextComponent("name", NameplateSlot.Name)
        {
            OutlineThickness = 1f,
            Attach = new NameplateAttach(AttachPoint.BottomLeft, null, AttachPoint.TopLeft, new Vector2(0f, -2f)),
        },
        new NameplateBadgeComponent("level", NameplateSlot.Level)
        {
            OutlineThickness = 1f,
            PadX = 3f,
            Attach = new NameplateAttach(AttachPoint.BottomRight, null, AttachPoint.TopRight, new Vector2(0f, -2f)),
        },
    });

    [Fact]
    public void AFullHeaderPlacesNameBadgeAndBar()
    {
        var content = FullHeader();
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, HeaderTemplate, content, resolution));

        Assert.True(resolution.Components[0].Drawn);
        Assert.True(resolution.Components[1].Drawn);
        Assert.True(resolution.Components[2].Drawn);
        Assert.Equal(new Rect(0f, 0f, 114f, 11f), resolution.Components[0].Rect);
        Assert.Equal(new Rect(0f, -14f, 38f, 12f), resolution.Components[1].Rect);
        Assert.Equal(new Rect(94f, -14f, 20f, 12f), resolution.Components[2].Rect);
        Assert.Equal("Zombie", resolution.Components[1].Text);
        Assert.Equal("12", resolution.Components[2].Text);
        Assert.Equal(38f, resolution.ContentWidth);
        Assert.Equal(new Rect(0f, -14f, 114f, 25f), resolution.Bounds);
        Assert.Equal(new Vector2(114f, 25f), NameplateTemplateLayout.Measure(Font, HeaderTemplate, content));
    }

    [Fact]
    public void AMissingLevelSkipsTheBadge()
    {
        var content = FullHeader();
        content.SetText(NameplateSlot.Level, null, Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, HeaderTemplate, content, resolution));

        Assert.False(resolution.Components[2].Drawn);
        Assert.Equal(new Rect(114f, -2f, 0f, 0f), resolution.Components[2].Rect);
        Assert.Equal(new Rect(0f, -14f, 114f, 25f), resolution.Bounds);
    }

    [Fact]
    public void ABadgeWithoutANameStillHangsOffTheBar()
    {
        var content = FullHeader();
        content.SetText(NameplateSlot.Name, "", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, HeaderTemplate, content, resolution));

        Assert.False(resolution.Components[1].Drawn);
        Assert.Equal(new Rect(0f, -2f, 0f, 0f), resolution.Components[1].Rect);
        Assert.Equal(new Rect(94f, -14f, 20f, 12f), resolution.Components[2].Rect);
        Assert.Equal(new Rect(0f, -14f, 114f, 25f), resolution.Bounds);
    }

    [Fact]
    public void NothingSetMeasuresZero()
    {
        var template = HeaderTemplate with { MinContentWidth = 90f, PadX = 5f, PadY = 3f };
        var content = new NameplateContent();
        var resolution = new NameplateResolution();

        Assert.False(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.False(resolution.Components[0].Drawn);
        Assert.False(resolution.Components[1].Drawn);
        Assert.False(resolution.Components[2].Drawn);
        Assert.Equal(default, resolution.Bounds);
        Assert.Equal(Vector2.Zero, NameplateTemplateLayout.Measure(Font, template, content));
    }

    [Fact]
    public void ANameOverItsMaxWidthIsEllipsisedAndTheBadgeIsNot()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            HeaderTemplate.Components[0],
            (NameplateTextComponent)HeaderTemplate.Components[1] with { MaxWidth = 60f },
            HeaderTemplate.Components[2],
        });
        var content = FullHeader();
        content.SetText(NameplateSlot.Name, "Abcdefghijkl", Color.White);
        content.SetText(NameplateSlot.Level, "123", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal("Abcdef...", resolution.Components[1].Text);
        Assert.Equal(new Rect(0f, -14f, 56f, 12f), resolution.Components[1].Rect);
        Assert.Equal("123", resolution.Components[2].Text);
        Assert.Equal(new Rect(88f, -14f, 26f, 12f), resolution.Components[2].Rect);
        Assert.Equal(74f, resolution.ContentWidth);
    }

    [Fact]
    public void TheContentCapWinsOverTheFloor()
    {
        var template = TextTemplate() with { MinContentWidth = 120f, MaxContentWidth = 50f };
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Abcdefghijkl", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(50f, resolution.ContentWidth);
        Assert.Equal("Abcde...", resolution.Components[0].Text);
        Assert.Equal(new Rect(0f, 0f, 48f, 10f), resolution.Components[0].Rect);
        Assert.Equal(new Rect(-1f, 0f, 50f, 10f), resolution.Bounds);
    }

    [Fact]
    public void TheFloorWidensTheBounds()
    {
        var template = TextTemplate() with { MinContentWidth = 90f };
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Goblin", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(new Rect(0f, 0f, 36f, 10f), resolution.Components[0].Rect);
        Assert.Equal(new Rect(-27f, 0f, 90f, 10f), resolution.Bounds);
        Assert.Equal(new Vector2(90f, 10f), NameplateTemplateLayout.Measure(Font, template, content));
    }

    [Fact]
    public void AZeroWidthBarTakesTheContentWidth()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBarComponent("health", NameplateSlot.Health)
            {
                Width = 0f,
                Height = 7f,
                Attach = new NameplateAttach(AttachPoint.Top, null, AttachPoint.Bottom, new Vector2(0f, 3f)),
            },
        }) { MinContentWidth = 90f };
        var content = FullHeader();
        content.SetText(NameplateSlot.Name, "Goblin", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(new Rect(-27f, 13f, 90f, 7f), resolution.Components[1].Rect);
        Assert.Equal(new Rect(-27f, 0f, 90f, 20f), resolution.Bounds);
    }

    [Fact]
    public void AWarmResolveAllocatesNothing()
    {
        var content = FullHeader();
        NameplateTemplateLayout.Measure(Font, HeaderTemplate, content);
        NameplateTemplateLayout.Measure(Font, HeaderTemplate, content);
        GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Vector2 size = NameplateTemplateLayout.Measure(Font, HeaderTemplate, content);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(new Vector2(114f, 25f), size);
        Assert.Equal(before, after);
    }

    [Fact]
    public void PanelPaddingIsAddedAfterTheComponentBounds()
    {
        var template = HeaderTemplate with { PadX = 5f, PadY = 3f };
        var content = FullHeader();
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(new Rect(0f, -14f, 114f, 25f), resolution.Bounds);
        Assert.Equal(new Vector2(124f, 31f), NameplateTemplateLayout.Measure(Font, template, content));
    }

    [Fact]
    public void SlotsHoldingTheWrongKindDoNotDraw()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Health, "health", Color.White);
        content.SetBar(NameplateSlot.Name, 0.6f, Color.White, Color.Black);
        content.SetBar(NameplateSlot.Level, 0.6f, Color.White, Color.Black);
        var resolution = new NameplateResolution();

        Assert.False(NameplateTemplateLayout.Resolve(Font, HeaderTemplate, content, resolution));

        Assert.False(resolution.Components[0].Drawn);
        Assert.False(resolution.Components[1].Drawn);
        Assert.False(resolution.Components[2].Drawn);
        Assert.Equal(0f, resolution.ContentWidth);
        Assert.Equal(Vector2.Zero, NameplateTemplateLayout.Measure(Font, HeaderTemplate, content));
    }

    [Fact]
    public void ADependentUsesTheSkippedTargetsAttachPoint()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("level", NameplateSlot.Level)
            {
                Attach = new NameplateAttach(AttachPoint.BottomRight, null, AttachPoint.TopRight, new Vector2(4f, -2f)),
            },
            new NameplateBarComponent("health", NameplateSlot.Health)
            {
                Width = 8f,
                Height = 3f,
                Attach = new NameplateAttach(AttachPoint.TopLeft, "level", AttachPoint.Bottom, new Vector2(2f, 1f)),
            },
        });
        var content = FullHeader();
        content.SetText(NameplateSlot.Name, "Goblin", Color.White);
        content.SetText(NameplateSlot.Level, null, Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(new Rect(40f, -2f, 0f, 0f), resolution.Components[1].Rect);
        Assert.Equal(new Rect(42f, -1f, 8f, 3f), resolution.Components[2].Rect);
        Assert.Equal(new Rect(0f, -1f, 50f, 11f), resolution.Bounds);
    }

    [Fact]
    public void AMissingRootDoesNotAddTheOriginToTheBounds()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Level, "12", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, HeaderTemplate, content, resolution));

        Assert.Equal(new Rect(0f, 0f, 0f, 0f), resolution.Components[0].Rect);
        Assert.Equal(new Rect(-20f, -14f, 20f, 12f), resolution.Components[2].Rect);
        Assert.Equal(new Rect(-20f, -14f, 20f, 12f), resolution.Bounds);
    }

    [Theory]
    [InlineData(60f, 40f, "Abc...", 38f)]
    [InlineData(40f, 60f, "Abc...", 38f)]
    [InlineData(2f, 50f, "Abcde...", 50f)]
    public void TextUsesTheSmallestPositiveGlyphCap(float maxWidth, float contentCap, string text, float width)
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name)
            {
                OutlineThickness = 1f,
                MaxWidth = maxWidth,
            },
        }) { MaxContentWidth = contentCap };
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Abcdefghijkl", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(text, resolution.Components[0].Text);
        Assert.Equal(width, resolution.Components[0].Rect.Width);
    }

    [Fact]
    public void BadgesKeepTheirNaturalSizeBeyondTheContentCap()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateBadgeComponent("level", NameplateSlot.Level)
            {
                FontScale = 2f,
                OutlineThickness = 1f,
                MinWidth = 50f,
                PadX = 3f,
                PadY = 2f,
            },
        }) { MaxContentWidth = 10f };
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Level, "123", Color.White);
        var resolution = new NameplateResolution();

        Assert.True(NameplateTemplateLayout.Resolve(Font, template, content, resolution));

        Assert.Equal(10f, resolution.ContentWidth);
        Assert.Equal("123", resolution.Components[0].Text);
        Assert.Equal(new Rect(0f, 0f, 50f, 26f), resolution.Components[0].Rect);
        Assert.Equal(new Rect(0f, 0f, 50f, 26f), resolution.Bounds);
    }

    [Fact]
    public void ReusingScratchClearsDrawnTextAndBounds()
    {
        var content = FullHeader();
        var resolution = new NameplateResolution();
        Assert.True(NameplateTemplateLayout.Resolve(Font, HeaderTemplate, content, resolution));
        content.Clear();

        Assert.False(NameplateTemplateLayout.Resolve(Font, TextTemplate(), content, resolution));

        Assert.Equal(1, resolution.Components.Length);
        Assert.False(resolution.Components[0].Drawn);
        Assert.Equal(string.Empty, resolution.Components[0].Text);
        Assert.Equal(default, resolution.Bounds);
        Assert.Equal(0f, resolution.ContentWidth);
    }

    private static NameplateTemplate TextTemplate() => new(new NameplateComponent[]
    {
        new NameplateTextComponent("name", NameplateSlot.Name),
    });

    private static NameplateContent FullHeader()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        content.SetText(NameplateSlot.Level, "12", Color.White);
        content.SetBar(NameplateSlot.Health, 0.6f, Color.White, Color.Black);
        return content;
    }
}
