using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;
using static KhaozEngine.Tests.Render2D.SpriteBatchMaskedTestRig;

namespace KhaozEngine.Tests.Render2D;

public class SpriteBatchMaskedVertexTests
{
    [Fact]
    public void SourceAndMaskRegionsHaveIndependentCorners()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        var tint = new Color(0.2f, 0.4f, 0.6f, 0.8f);
        rig.Frame(batch => batch.DrawMasked(source, new Vector4(10, 20, 30, 40),
            new Vector4(0.125f, 0.25f, 0.625f, 0.75f), mask,
            new Vector4(0.3f, 0.4f, 0.7f, 0.9f), tint));

        var singleQuadUpload = Assert.Single(rig.VertexUploads).Data;
        var vertices = Vertices(rig.VertexUploads[0]);
        Assert.Equal(new[] { new Vector2(10, 20), new Vector2(40, 20), new Vector2(40, 60),
            new Vector2(10, 20), new Vector2(40, 60), new Vector2(10, 60) }, vertices.Select(v => v.Pos));
        Assert.Equal(new[] { new Vector2(0.125f, 0.25f), new Vector2(0.625f, 0.25f),
            new Vector2(0.625f, 0.75f), new Vector2(0.125f, 0.25f),
            new Vector2(0.625f, 0.75f), new Vector2(0.125f, 0.75f) }, vertices.Select(v => v.Uv));
        Assert.Equal(new[] { new Vector2(0.3f, 0.4f), new Vector2(0.7f, 0.4f),
            new Vector2(0.7f, 0.9f), new Vector2(0.3f, 0.4f),
            new Vector2(0.7f, 0.9f), new Vector2(0.3f, 0.9f) }, vertices.Select(v => v.Local));
        Assert.All(vertices, v =>
        {
            Assert.Equal((Vector4)tint, v.Color);
            Assert.Equal(Vector4.Zero, v.Shape);
            Assert.Equal(Vector2.Zero, v.Mode);
        });
        Assert.Equal(384, singleQuadUpload.Length);
        Assert.Equal(1, rig.Batch.FrameStats.Quads);
        Assert.Equal(1, rig.Batch.FrameStats.DrawCalls);
        Assert.Equal(1, rig.Batch.FrameStats.Flushes);
        Assert.Equal(384, rig.Batch.FrameStats.SpriteUploadBytes);
    }

    [Fact]
    public void NullTexturesAreRejectedBeforeCreatingMaskedResources()
    {
        using var rig = Create();
        using var sprite = Sprite();
        rig.Frame(batch =>
        {
            Assert.Equal("tex", Assert.Throws<ArgumentNullException>(() => batch.DrawMasked(null!,
                Vector4.One, Vector4.One, sprite, Vector4.One, Color.White)).ParamName);
            Assert.Equal("mask", Assert.Throws<ArgumentNullException>(() => batch.DrawMasked(sprite,
                Vector4.One, Vector4.One, null!, Vector4.One, Color.White)).ParamName);
        });
        Assert.Null(rig.MaskedOwner);
        Assert.Empty(rig.Draws);
        Assert.Equal(0, rig.Batch.FrameStats.Quads);
    }
}
