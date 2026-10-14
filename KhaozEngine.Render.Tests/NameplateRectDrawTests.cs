using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class NameplateRectDrawTests
{
    [Theory]
    [InlineData(NameplateEdgeBehavior.Clamp)]
    [InlineData(NameplateEdgeBehavior.Deflect)]
    public void TheSuppliedRectBypassesClampAndDeflect(NameplateEdgeBehavior edgeBehavior)
    {
        var canvas = Canvas();

        bool drawn = NameplateRenderer.DrawTemplateAtRect(ref canvas, new Vector4(-20f, -8f, 18f, 14f),
            SimpleTemplate() with { EdgeBehavior = edgeBehavior }, SimpleContent(), 100, 100);

        Assert.True(drawn);
        Assert.Equal(new[]
        {
            "fill -20.000 -8.000 18.000 14.000 0.000 0.000 0.000 1.000 0.000",
            "text \"Ox\" -17.000 -6.000 1.000 1.000 1.000 1.000 1.000",
        }, canvas.Calls);
    }

    [Fact]
    public void DesignViewportUsesTheSuppliedDesignRectWithoutRemapping()
    {
        var viewport = new DesignViewport(1280, 720, ScaleMode.Fit);
        viewport.Update(2560, 1080);
        var pixelCanvas = Canvas();
        var designCanvas = Canvas();
        var rect = new Vector4(-20f, -8f, 18f, 14f);
        var template = SimpleTemplate() with { EdgeBehavior = NameplateEdgeBehavior.Clamp };
        var content = SimpleContent();

        Assert.True(NameplateRenderer.DrawTemplateAtRect(ref pixelCanvas, rect, template, content, 100, 100));
        Assert.True(NameplateRenderer.DrawTemplateAtRect(ref designCanvas, rect, template, content, viewport));

        Assert.Equal(new[]
        {
            "fill -20.000 -8.000 18.000 14.000 0.000 0.000 0.000 1.000 0.000",
            "text \"Ox\" -17.000 -6.000 1.000 1.000 1.000 1.000 1.000",
        }, designCanvas.Calls);
        Assert.Equal(pixelCanvas.Calls, designCanvas.Calls);
    }

    [Fact]
    public void RectDrawingPreservesEveryExistingComponentCall()
    {
        var oldCanvas = Canvas();
        var rectCanvas = Canvas();
        var template = PanelAndShadowTemplate();
        var content = FullHeader();
        var anchor = new Vector2(500f, 500f);
        NameplatePlacementState state = default;

        bool oldDrawn = NameplateRenderer.DrawTemplateAt(ref oldCanvas, anchor, template, content,
            1000, 1000, ref state);
        Vector2 size = NameplateTemplateLayout.Measure(oldCanvas.Font, template, content);
        Vector4 rect = NameplatePlacement.Place(anchor, size, 1000, 1000, template, ref state);
        bool rectDrawn = NameplateRenderer.DrawTemplateAtRect(ref rectCanvas, rect, template, content, 1000, 1000);

        Assert.True(oldDrawn);
        Assert.True(rectDrawn);
        Assert.Equal(10, rectCanvas.Calls.Count);
        Assert.Equal(oldCanvas.Calls, rectCanvas.Calls);
    }

    [Theory]
    [InlineData(true, 0f, 0f, 18f, 14f, 100, 100)]
    [InlineData(false, 0f, 0f, 0f, 14f, 100, 100)]
    [InlineData(false, 0f, 0f, 18f, -1f, 100, 100)]
    [InlineData(false, float.NaN, 0f, 18f, 14f, 100, 100)]
    [InlineData(false, 0f, float.PositiveInfinity, 18f, 14f, 100, 100)]
    [InlineData(false, 0f, 0f, float.PositiveInfinity, 14f, 100, 100)]
    [InlineData(false, 0f, 0f, 18f, float.NaN, 100, 100)]
    [InlineData(false, float.MaxValue, 0f, float.MaxValue, 14f, 100, 100)]
    [InlineData(false, 0f, float.MaxValue, 18f, float.MaxValue, 100, 100)]
    [InlineData(false, 0f, 0f, 18f, 14f, 0, 100)]
    [InlineData(false, 0f, 0f, 18f, 14f, 100, 0)]
    [InlineData(false, 0f, 0f, 18f, 14f, -1, 100)]
    [InlineData(false, 0f, 0f, 18f, 14f, 100, -1)]
    public void EmptyOrInvalidRectDrawsNothing(bool empty, float x, float y, float width, float height,
        int viewportWidth, int viewportHeight)
    {
        var canvas = Canvas();
        var content = empty ? new NameplateContent() : SimpleContent();

        bool drawn = NameplateRenderer.DrawTemplateAtRect(ref canvas, new Vector4(x, y, width, height),
            SimpleTemplate(), content, viewportWidth, viewportHeight);

        Assert.False(drawn);
        Assert.Empty(canvas.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NullResourcesReturnFalse(int missingResource)
    {
        // These sentinels must never be used. Validation has to return before touching a batch or font.
        var batch = missingResource == 0 ? null! : (SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
        var font = missingResource == 1 ? null! : (SpriteFont)RuntimeHelpers.GetUninitializedObject(typeof(SpriteFont));
        var white = missingResource == 2 ? null! : (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
        var template = missingResource == 3 ? null! : SimpleTemplate();
        var content = missingResource == 4 ? null! : SimpleContent();
        IDesignViewport viewport = missingResource == 5 ? null! : new DesignViewport(1280, 720, ScaleMode.Fit);
        var rect = new Vector4(0f, 0f, 18f, 14f);

        if (missingResource < 5)
        {
            bool drawn = NameplateRenderer.DrawAtRect(batch, font, white, rect, template, content, 100, 100);
            Assert.False(drawn);
        }
        bool designDrawn = NameplateRenderer.DrawAtRect(batch, font, white, rect, template, content, viewport);
        Assert.False(designDrawn);

        if (missingResource == 5)
        {
            var canvas = Canvas();
            bool drawn = NameplateRenderer.DrawTemplateAtRect(ref canvas, rect, template, content, viewport);
            Assert.False(drawn);
            Assert.Empty(canvas.Calls);
        }
    }

    [Fact]
    public void PublicRectOverloadsMatchTheAdoptionContract()
    {
        Type[] pixelDrawParameters =
        [
            typeof(SpriteBatch), typeof(SpriteFont), typeof(Texture2D), typeof(Vector4),
            typeof(NameplateTemplate), typeof(NameplateContent), typeof(int), typeof(int),
        ];
        Type[] designDrawParameters =
        [
            typeof(SpriteBatch), typeof(SpriteFont), typeof(Texture2D), typeof(Vector4),
            typeof(NameplateTemplate), typeof(NameplateContent), typeof(IDesignViewport),
        ];
        var pixelDraw = typeof(NameplateRenderer).GetMethod("DrawAtRect", pixelDrawParameters);
        var designDraw = typeof(NameplateRenderer).GetMethod("DrawAtRect", designDrawParameters);
        Assert.NotNull(pixelDraw);
        Assert.Equal(typeof(bool), pixelDraw.ReturnType);
        Assert.True(pixelDraw.IsStatic);
        Assert.NotNull(designDraw);
        Assert.Equal(typeof(bool), designDraw.ReturnType);
        Assert.True(designDraw.IsStatic);

        Assert.True(typeof(NameplateAnchorProjection).IsPublic);
        Type[] pixelProjectionParameters =
        [
            typeof(IIsoCamera3D), typeof(Vector3), typeof(Vector3), typeof(int), typeof(int),
            typeof(Vector2).MakeByRefType(),
        ];
        Type[] designProjectionParameters =
        [
            typeof(IIsoCamera3D), typeof(Vector3), typeof(Vector3), typeof(IDesignViewport),
            typeof(Vector2).MakeByRefType(),
        ];
        var pixelProjection = typeof(NameplateAnchorProjection).GetMethod("Project", pixelProjectionParameters);
        var designProjection = typeof(NameplateAnchorProjection).GetMethod("Project", designProjectionParameters);
        Assert.NotNull(pixelProjection);
        Assert.Equal(typeof(bool), pixelProjection.ReturnType);
        Assert.True(pixelProjection.IsPublic);
        Assert.True(pixelProjection.IsStatic);
        Assert.NotNull(designProjection);
        Assert.Equal(typeof(bool), designProjection.ReturnType);
        Assert.True(designProjection.IsPublic);
        Assert.True(designProjection.IsStatic);
    }

    [Fact]
    public void AWarmRectDrawAllocatesNothing()
    {
        var template = PanelAndShadowTemplate();
        var content = FullHeader();
        var canvas = new CountingNameplateCanvas(new FixedGlyphFont(6f, 10f));
        var rect = new Vector4(438f, 469f, 124f, 31f);
        Assert.True(NameplateRenderer.DrawTemplateAtRect(ref canvas, rect, template, content, 1000, 1000));
        Assert.True(NameplateRenderer.DrawTemplateAtRect(ref canvas, rect, template, content, 1000, 1000));
        int warmCalls = canvas.Calls;
        GC.GetAllocatedBytesForCurrentThread();

        long before, after;
        bool drawn;
        using (AllocationRegion.Start("a warm rect template draw with an outlined name and badge"))
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            drawn = NameplateRenderer.DrawTemplateAtRect(ref canvas, rect, template, content, 1000, 1000);
            after = GC.GetAllocatedBytesForCurrentThread();
        }

        Assert.True(drawn);
        Assert.Equal(warmCalls + 10, canvas.Calls);
        Assert.Equal(before, after);
    }

    private static RecordingNameplateCanvas Canvas() => new(new FixedGlyphFont(6f, 10f));

    private static NameplateTemplate SimpleTemplate() => new(new NameplateComponent[]
    {
        new NameplateTextComponent("name", NameplateSlot.Name),
    })
    {
        PanelFill = Color.Black,
        PadX = 3f,
        PadY = 2f,
        EdgeMargin = 4f,
    };

    private static NameplateContent SimpleContent()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Ox", Color.White);
        return content;
    }

    private static NameplateTemplate PanelAndShadowTemplate() => new(new NameplateComponent[]
    {
        NameplateTemplateLayoutTests.HeaderTemplate.Components[0],
        (NameplateTextComponent)NameplateTemplateLayoutTests.HeaderTemplate.Components[1] with
        {
            OutlineColor = Color.Black,
            ShadowColor = new Color(0.2f, 0.3f, 0.4f, 0.6f),
            ShadowOffset = new Vector2(2f, 3f),
        },
        (NameplateBadgeComponent)NameplateTemplateLayoutTests.HeaderTemplate.Components[2] with
        {
            OutlineColor = Color.Black,
        },
    })
    {
        PanelFill = new Color(0.1f, 0.2f, 0.3f, 0.8f),
        PanelBorder = new Color(0.4f, 0.5f, 0.6f, 0.6f),
        PanelBorderThickness = 2f,
        CornerRadius = 3f,
        PadX = 5f,
        PadY = 3f,
    };

    private static NameplateContent FullHeader()
    {
        var content = new NameplateContent { Opacity = 0.5f };
        content.SetText(NameplateSlot.Name, "Zombie", new Color(1f, 1f, 1f, 0.8f));
        content.SetText(NameplateSlot.Level, "12", new Color(1f, 0.8f, 0.2f, 0.6f), new Color(1f, 0f, 0f, 0.2f));
        content.SetBar(NameplateSlot.Health, 0.6f, new Color(0.2f, 0.8f, 0.3f, 0.9f), new Color(0.15f, 0.15f, 0.15f, 0.8f));
        return content;
    }
}
