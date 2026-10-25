using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;
using static KhaozEngine.Tests.Render2D.SpriteBatchMaskedTestRig;

namespace KhaozEngine.Tests.Render2D;

public class SpriteBatchMaskedVertexTests
{
    [Fact]
    public void RectAndWholeTextureOverloadsForwardExactly()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        var dest = new Vector4(10, 20, 30, 40);
        var rect = new Rect(10, 20, 30, 40);
        var sourceUv = new Vector4(0.625f, 0.25f, 0.125f, 0.75f);
        var maskUv = new Vector4(0.3f, 0.9f, 0.7f, 0.4f);
        var fullUv = new Vector4(0, 0, 1, 1);
        var top = new Color(1f, 0.5f, 0f, 0.25f);
        var bottom = new Color(0f, 0.25f, 1f, 0.75f);

        foreach (var sampler in new[] { SamplerMode.Linear, SamplerMode.Point })
        {
            Compare(batch => batch.DrawMasked(source, dest, sourceUv, mask, maskUv, top, top, sampler),
                batch => batch.DrawMasked(source, dest, sourceUv, mask, maskUv, top, sampler));
            Compare(batch => batch.DrawMasked(source, dest, sourceUv, mask, maskUv, top, sampler),
                batch => batch.DrawMasked(source, rect, sourceUv, mask, maskUv, top, sampler));
            Compare(batch => batch.DrawMasked(source, dest, sourceUv, mask, maskUv, top, bottom, sampler),
                batch => batch.DrawMasked(source, rect, sourceUv, mask, maskUv, top, bottom, sampler));
            Compare(batch => batch.DrawMasked(source, dest, fullUv, mask, fullUv, top, sampler),
                batch => batch.DrawMasked(source, dest, mask, top, sampler));
            Compare(batch => batch.DrawMasked(source, dest, fullUv, mask, fullUv, top, sampler),
                batch => batch.DrawMasked(source, rect, mask, top, sampler));
        }

        void Compare(Action<SpriteBatch> explicitDraw, Action<SpriteBatch> convenienceDraw)
        {
            object explicitKey = null!, convenienceKey = null!;
            rig.Frame(batch => { explicitDraw(batch); explicitKey = PendingKey(batch); }, SamplerMode.Point);
            byte[] explicitBytes = Assert.Single(rig.VertexUploads).Data;
            rig.Frame(batch => { convenienceDraw(batch); convenienceKey = PendingKey(batch); }, SamplerMode.Point);
            byte[] convenienceBytes = Assert.Single(rig.VertexUploads).Data;
            Assert.Equal(explicitBytes, convenienceBytes);
            Assert.Same(explicitKey, convenienceKey);
        }
    }

    static object PendingKey(SpriteBatch batch)
    {
        var runs = typeof(SpriteBatch).GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
        var pending = Assert.IsAssignableFrom<IReadOnlyList<(object Key, int Start, int Count)>>(
            runs.GetType().GetProperty("Runs")!.GetValue(runs));
        return Assert.Single(pending).Key;
    }

    [Fact]
    public void GradientPreservesAllFourTintChannels()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch => batch.DrawMasked(source, new Vector4(10, 20, 30, 40),
            new Vector4(0, 0, 1, 1), mask, new Vector4(0, 0, 1, 1),
            new Color(1f, 0.5f, 0f, 0.25f), new Color(0f, 0.25f, 1f, 0.75f)));

        var vertices = Vertices(Assert.Single(rig.VertexUploads));
        Assert.Equal(new Vector4(1, 0.5f, 0, 0.25f), vertices[0].Color);
        Assert.Equal(new Vector4(0, 0.25f, 1, 0.75f), vertices[2].Color);
        Assert.Equal(vertices[0].Color, vertices[1].Color);
        Assert.Equal(vertices[0].Color, vertices[3].Color);
        Assert.Equal(vertices[2].Color, vertices[4].Color);
        Assert.Equal(vertices[2].Color, vertices[5].Color);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SourceOrMaskUvCanBeReversedIndependently(bool reverseSource, bool reverseMask)
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        var sourceUv = reverseSource ? new Vector4(0.625f, 0.75f, 0.125f, 0.25f) : new Vector4(0.125f, 0.25f, 0.625f, 0.75f);
        var maskUv = reverseMask ? new Vector4(0.7f, 0.9f, 0.3f, 0.4f) : new Vector4(0.3f, 0.4f, 0.7f, 0.9f);
        rig.Frame(batch => batch.DrawMasked(source, new Vector4(10, 20, 30, 40), sourceUv,
            mask, maskUv, Color.White));

        Vector2[] expectedSource = reverseSource
            ? [new(0.625f, 0.75f), new(0.125f, 0.75f), new(0.125f, 0.25f), new(0.625f, 0.75f), new(0.125f, 0.25f), new(0.625f, 0.25f)]
            : [new(0.125f, 0.25f), new(0.625f, 0.25f), new(0.625f, 0.75f), new(0.125f, 0.25f), new(0.625f, 0.75f), new(0.125f, 0.75f)];
        Vector2[] expectedMask = reverseMask
            ? [new(0.7f, 0.9f), new(0.3f, 0.9f), new(0.3f, 0.4f), new(0.7f, 0.9f), new(0.3f, 0.4f), new(0.7f, 0.4f)]
            : [new(0.3f, 0.4f), new(0.7f, 0.4f), new(0.7f, 0.9f), new(0.3f, 0.4f), new(0.7f, 0.9f), new(0.3f, 0.9f)];
        var vertices = Vertices(Assert.Single(rig.VertexUploads));
        Assert.Equal(expectedSource, vertices.Select(vertex => vertex.Uv));
        Assert.Equal(expectedMask, vertices.Select(vertex => vertex.Local));
    }

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
    public void NullTexturesNameTheArgument()
    {
        using var rig = Create();
        using var sprite = Sprite();
        rig.Frame(batch =>
        {
            var rect = new Rect(1, 2, 3, 4);
            Action<Texture2D, Texture2D>[] draws =
            [
                (source, mask) => batch.DrawMasked(source, Vector4.One, Vector4.One, mask, Vector4.One, Color.White),
                (source, mask) => batch.DrawMasked(source, Vector4.One, Vector4.One, mask, Vector4.One, Color.White, new Color(1f, 0f, 0f, 1f)),
                (source, mask) => batch.DrawMasked(source, Vector4.One, mask, Color.White),
                (source, mask) => batch.DrawMasked(source, rect, Vector4.One, mask, Vector4.One, Color.White),
                (source, mask) => batch.DrawMasked(source, rect, Vector4.One, mask, Vector4.One, Color.White, new Color(1f, 0f, 0f, 1f)),
                (source, mask) => batch.DrawMasked(source, rect, mask, Color.White),
            ];
            foreach (var draw in draws)
            {
                Action nullSourceDraw = () => draw(null!, sprite);
                Action nullMaskDraw = () => draw(sprite, null!);
                Assert.Throws<ArgumentNullException>("tex", nullSourceDraw);
                Assert.Throws<ArgumentNullException>("mask", nullMaskDraw);
            }
        });
        Assert.Null(rig.MaskedOwner);
        Assert.Empty(rig.Draws);
        Assert.Equal(0, rig.Batch.FrameStats.Quads);
    }
}
