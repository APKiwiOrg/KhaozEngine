using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using static KhaozEngine.Tests.Render2D.SpriteBatchMaskedTestRig;

namespace KhaozEngine.Tests.Render2D;

public class SpriteBatchMaskedBatchTests
{
    [Fact]
    public void UnmaskedUploadsAndCommandsMatchCharacterization()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        using var font = SpriteFont.Build(rig.Device, DefaultFont.Bytes, 12);
        Assert.Null(rig.MaskedOwner);

        var legacy = rig.CaptureUnmaskedTrace(source, font);
        var ordinaryPipelines = rig.Draws.Select(draw => draw.Pipeline).ToArray();
        var ordinaryBindings = rig.Draws.Select(draw => (draw.Set0, draw.Set1)).ToArray();
        Assert.Null(rig.MaskedOwner);
        Assert.Single(rig.Factory.ShaderRequests);
        Assert.Equal(2, rig.Factory.GraphicsPipelines.Count);
        Assert.Equal(6, rig.Batch.FrameStats.Quads);
        Assert.Equal(new uint[] { 18, 12, 6 }, rig.Draws.Select(draw => draw.VertexCount));
        Assert.Equal(new[] { "Sprite2D:alpha:source:point:2:vp:0", "Sprite2D:alpha:font:point:2:vp:0",
            "Sprite2D:additive:source:point:2:vp:0" }, legacy.Commands.TakeLast(3));

        rig.Frame(batch => Masked(batch, source, mask));
        Assert.NotNull(rig.MaskedOwner);
        var warmed = rig.CaptureUnmaskedTrace(source, font);
        byte[] legacyVertexBytes = legacy.VertexBytes, warmedVertexBytes = warmed.VertexBytes;
        byte[] legacyVpBytes = legacy.VpBytes, warmedVpBytes = warmed.VpBytes;
        string[] legacyCommandTrace = legacy.Commands, warmedCommandTrace = warmed.Commands;
        Assert.Equal(legacyVertexBytes, warmedVertexBytes);
        Assert.Equal(legacyVpBytes, warmedVpBytes);
        Assert.Equal(legacyCommandTrace, warmedCommandTrace);
        Assert.All(rig.Draws, draw =>
        {
            int ordinaryTextureSetResourceCount = Assert.IsType<FakeResourceSet>(draw.Set0).Resources.Length;
            Assert.Equal(2, ordinaryTextureSetResourceCount);
        });
        for (int i = 0; i < rig.Draws.Count; i++)
        {
            Assert.Same(ordinaryPipelines[i], rig.Draws[i].Pipeline);
            Assert.Same(ordinaryBindings[i].Set0, rig.Draws[i].Set0);
            Assert.Same(ordinaryBindings[i].Set1, rig.Draws[i].Set1);
        }
        int ordinaryQuadCount = rig.Batch.FrameStats.Quads;
        long ordinaryUploadedBytes = rig.Batch.FrameStats.SpriteUploadBytes;
        Assert.Equal(384L * ordinaryQuadCount, ordinaryUploadedBytes);
        Assert.Equal(ordinaryUploadedBytes, warmedVertexBytes.LongLength);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("mask")]
    [InlineData("mask sampler")]
    [InlineData("blend")]
    public void GroupingUsesTheCompleteMaskedKey(string changed)
    {
        using var rig = Create();
        using var source = Sprite();
        using var secondSource = Sprite();
        using var mask = Sprite();
        using var secondMask = Sprite();
        void DrawAba(SpriteBatch batch)
        {
            batch.DrawMasked(source, new Vector4(0, 0, 1, 1), mask, Color.White);
            batch.BlendMode = changed == "blend" ? BlendMode.Additive : BlendMode.Alpha;
            batch.DrawMasked(changed == "source" ? secondSource : source, new Vector4(1, 0, 1, 1),
                changed == "mask" ? secondMask : mask, Color.White,
                changed == "mask sampler" ? SamplerMode.Point : SamplerMode.Linear);
            batch.BlendMode = BlendMode.Alpha;
            // UVs and tint belong to vertices, so the third quad still joins the first key.
            batch.DrawMasked(source, new Vector4(2, 0, 1, 1), new Vector4(1, 1, 0, 0),
                mask, new Vector4(1, 0, 0, 1), new Color(1f, 0f, 0f, 0.5f));
        }

        rig.Frame(DrawAba);
        var ungroupedABADraws = rig.Draws.ToList();
        Assert.Equal(3, ungroupedABADraws.Count);
        Assert.Equal(new[] { 0, 1, 2 }, rig.VertexUploads.SelectMany(OriginalQuadIndices));
        rig.Frame(DrawAba, group: true);
        var groupedABADraws = rig.Draws.ToList();
        Assert.Equal(2, groupedABADraws.Count);
        var firstGroupOriginalQuadIndices = OriginalQuadIndices(groupedABADraws[0], rig.VertexUploads);
        Assert.Equal(new[] { 0, 2 }, firstGroupOriginalQuadIndices);
        Assert.Equal(new[] { 1 }, OriginalQuadIndices(groupedABADraws[1], rig.VertexUploads));
        Assert.Equal(new uint[] { 12, 6 }, groupedABADraws.Select(draw => draw.VertexCount));
        Assert.Equal(new uint[] { 0, 12 }, groupedABADraws.Select(draw => draw.VertexStart));
    }

    [Fact]
    public void GroupedBeginsKeepSourceSamplerBindingsSeparate()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch =>
        {
            Masked(batch, source, mask);
            batch.End();
            batch.Begin(SamplerMode.Point);
            batch.GroupByTexture = true;
            Masked(batch, source, mask);
        }, group: true);

        Assert.Equal(2, rig.Draws.Count);
        var linearSet = Assert.IsType<FakeResourceSet>(rig.Draws[0].Set0);
        var pointSet = Assert.IsType<FakeResourceSet>(rig.Draws[1].Set0);
        Assert.NotSame(linearSet, pointSet);
        Assert.Same(rig.Device.LinearSampler, linearSet.Resources[1]);
        Assert.Same(rig.Device.PointSampler, pointSet.Resources[1]);
        Assert.Same(linearSet.Resources[0], pointSet.Resources[0]);
        Assert.Same(linearSet.Resources[2], pointSet.Resources[2]);
        Assert.Same(linearSet.Resources[3], pointSet.Resources[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlendModesSplitRunsButShareBindingSet(bool group)
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch =>
        {
            Masked(batch, source, mask);
            batch.BlendMode = BlendMode.Additive;
            Masked(batch, source, mask);
        }, group: group);

        Assert.Equal(2, rig.Draws.Count);
        var alphaSet = Assert.IsType<FakeResourceSet>(rig.Draws[0].Set0);
        var additiveSet = Assert.IsType<FakeResourceSet>(rig.Draws[1].Set0);
        Assert.Same(alphaSet, additiveSet);
        Assert.Same(rig.MaskedOwner!.PipelineFor(BlendMode.Alpha), rig.Draws[0].Pipeline);
        Assert.Same(rig.MaskedOwner.PipelineFor(BlendMode.Additive), rig.Draws[1].Pipeline);
        Assert.Equal(1, rig.Batch.CachedMaskedSetCount);
    }

    [Fact]
    public void TextAndOrdinaryDrawsStayInSubmissionOrder()
    {
        using var rig = Create();
        using var font = SpriteFont.Build(rig.Device, DefaultFont.Bytes, 12);
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch =>
        {
            batch.DrawString(font, "AB", new Vector2(1, 2), Color.White);
            Masked(batch, source, mask);
            batch.Draw(source, new Vector4(3, 4, 5, 6), Color.White);
        });

        var textMaskPanelShaderOrder = rig.Draws.Select(rig.ShaderName).ToArray();
        Assert.Equal(new[] { "Sprite2D", "Sprite2DMasked", "Sprite2D" }, textMaskPanelShaderOrder);
        Assert.Equal(new uint[] { 12, 6, 6 }, rig.Draws.Select(draw => draw.VertexCount));
        Assert.Equal(new uint[] { 0, 12, 18 }, rig.Draws.Select(draw => draw.VertexStart));
        Assert.Same(font.Atlas.Handle, Assert.IsType<FakeResourceSet>(rig.Draws[0].Set0).Resources[0]);
        Assert.Same(source.Handle, Assert.IsType<FakeResourceSet>(rig.Draws[2].Set0).Resources[0]);
        Assert.Equal(2, Assert.IsType<FakeResourceSet>(rig.Draws[2].Set0).Resources.Length);
    }

    [Fact]
    public void FlushAndScissorKeepTheActiveTransform()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        var transform = Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateTranslation(7, 11, 0);
        rig.Frame(batch =>
        {
            batch.End();
            batch.Begin(transform, SamplerMode.Point);
            batch.BlendMode = BlendMode.Additive;
            Masked(batch, source, mask);
            batch.Flush();
            Masked(batch, source, mask);
            batch.SetScissor(new Rect(1, 1, 6, 6));
            batch.SetScissor(new Rect(2, 2, 2, 2));
            Masked(batch, source, mask);
            batch.ClearScissor();
            Masked(batch, source, mask);
            batch.ClearScissor();
            Masked(batch, source, mask);
        });

        Assert.Equal(5, rig.Draws.Count);
        uint initialVpOffset = rig.Draws[0].DynamicOffset;
        uint afterFlushVpOffset = rig.Draws[1].DynamicOffset;
        uint afterNestedScissorVpOffset = rig.Draws[^1].DynamicOffset;
        Assert.NotEqual(0u, initialVpOffset);
        Assert.Equal(initialVpOffset, afterFlushVpOffset);
        Assert.Equal(initialVpOffset, afterNestedScissorVpOffset);
        Assert.All(rig.Draws, draw => Assert.Equal(initialVpOffset, draw.DynamicOffset));
        var pointSampler = rig.Device.PointSampler;
        var afterScissorSourceSampler = Assert.IsType<FakeResourceSet>(rig.Draws[^1].Set0).Resources[1];
        var afterScissorBlend = rig.Batch.BlendMode;
        Assert.Same(pointSampler, afterScissorSourceSampler);
        Assert.Equal(BlendMode.Additive, afterScissorBlend);
        Assert.All(rig.Draws, draw => Assert.Same(rig.MaskedOwner!.PipelineFor(BlendMode.Additive), draw.Pipeline));
        Assert.Equal(0, rig.Batch.ScissorDepth);
        Assert.Equal(2, rig.Commands.UniformUploads.Count);
        var vpUpload = rig.Commands.UniformUploads[1];
        Assert.Equal(0u, vpUpload.Offset);
        Assert.Equal(transform * Matrix4x4.CreateOrthographicOffCenter(0, 64, 64, 0, -1, 1),
            MemoryMarshal.Read<Matrix4x4>(vpUpload.Data.AsSpan((int)initialVpOffset)));
    }

    static FakeTexture Texture() => new(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);

    static void Masked(SpriteBatch batch, Texture2D source, Texture2D mask,
        SamplerMode maskSampler = SamplerMode.Linear) =>
        batch.DrawMasked(source, new Vector4(10, 20, 30, 40),
            new Vector4(0.125f, 0.25f, 0.625f, 0.75f), mask,
            new Vector4(0.3f, 0.4f, 0.7f, 0.9f), Color.White, maskSampler);

    [Fact]
    public void SourceAndMaskSamplerSelectionsAreIndependent()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        var pointSampler = rig.Device.PointSampler;
        var linearSampler = rig.Device.LinearSampler;
        rig.Frame(batch =>
        {
            batch.DrawMasked(source, Vector4.One, new Vector4(0, 0, 1, 1), mask,
                new Vector4(0, 0, 1, 1), Color.White);
            Masked(batch, source, mask, SamplerMode.Point);
        }, sourceSampler: SamplerMode.Point);

        Assert.Equal(2, rig.Draws.Count);
        var defaultMaskSet = Assert.IsType<FakeResourceSet>(rig.Draws[0].Set0);
        var explicitPointMaskSet = Assert.IsType<FakeResourceSet>(rig.Draws[1].Set0);
        Assert.Same(pointSampler, defaultMaskSet.Resources[1]);
        Assert.Same(linearSampler, defaultMaskSet.Resources[3]);
        Assert.Same(pointSampler, explicitPointMaskSet.Resources[1]);
        Assert.Same(pointSampler, explicitPointMaskSet.Resources[3]);
        Assert.NotSame(defaultMaskSet, explicitPointMaskSet);

        rig.Frame(batch =>
        {
            batch.DrawMasked(source, Vector4.One, new Vector4(0, 0, 1, 1), mask,
                new Vector4(0, 0, 1, 1), Color.White);
            Masked(batch, source, mask, SamplerMode.Point);
        }, sourceSampler: SamplerMode.Linear);
        Assert.Equal(2, rig.Draws.Count);
        var linearSourceSet = Assert.IsType<FakeResourceSet>(rig.Draws[0].Set0);
        var linearSourcePointMaskSet = Assert.IsType<FakeResourceSet>(rig.Draws[1].Set0);
        Assert.Same(linearSampler, linearSourceSet.Resources[1]);
        Assert.Same(linearSampler, linearSourceSet.Resources[3]);
        Assert.Same(linearSampler, linearSourcePointMaskSet.Resources[1]);
        Assert.Same(pointSampler, linearSourcePointMaskSet.Resources[3]);
        Assert.NotSame(defaultMaskSet, linearSourceSet);
        Assert.NotSame(explicitPointMaskSet, linearSourcePointMaskSet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingOnlyUvsOrTintDoesNotSplit(bool group)
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch =>
        {
            Masked(batch, source, mask);
            batch.DrawMasked(source, new Vector4(10, 20, 30, 40), new Vector4(1, 0, 0, 1),
                mask, new Vector4(0, 1, 1, 0), Color.White);
            batch.DrawMasked(source, new Vector4(10, 20, 30, 40), new Vector4(0, 0, 1, 1),
                mask, new Vector4(0, 0, 1, 1),
                new Color(1f, 0.5f, 0f, 0.25f), new Color(0f, 0.25f, 1f, 0.75f));
        }, group: group);

        int uvTintOnlyDrawCount = rig.Draws.Count;
        Assert.Equal(1, uvTintOnlyDrawCount);
        Assert.Equal(18u, rig.Draws[0].VertexCount);
        Assert.Equal(3, rig.Batch.FrameStats.Quads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveSameBindingCoalesces(bool group)
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch => { Masked(batch, source, mask); Masked(batch, source, mask); },
            sourceSampler: SamplerMode.Point, group: group);

        int sameTupleDrawCount = rig.Draws.Count;
        Assert.Equal(1, sameTupleDrawCount);
        var draw = Assert.Single(rig.Draws);
        Assert.Equal(12u, draw.VertexCount);
        Assert.Equal(0u, draw.VertexStart);
        Assert.Equal(1u, draw.InstanceCount);
        Assert.Equal(0u, draw.InstanceStart);
        Assert.Same(rig.VertexUploads[0].Buffer, draw.VertexBuffer);
        Assert.Same(rig.Factory.ResourceSets[0], draw.Set1);
        Assert.Equal(0u, draw.DynamicOffset);
        Assert.Equal(new IGpuBindableResource[] { source.Handle, rig.Device.PointSampler,
            mask.Handle, rig.Device.LinearSampler }, Assert.IsType<FakeResourceSet>(draw.Set0).Resources);
        Assert.Same(rig.MaskedOwner!.PipelineFor(BlendMode.Alpha), draw.Pipeline);
        Assert.Equal(2, rig.Batch.FrameStats.Quads);
        Assert.Equal(1, rig.Batch.FrameStats.Flushes);
        Assert.Equal(768, rig.Batch.FrameStats.SpriteUploadBytes);
    }

    [Fact]
    public void DifferentMasksSplitAndPreserveOrder()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        rig.Frame(batch =>
        {
            Masked(batch, source, maskA);
            Masked(batch, source, maskB);
            Masked(batch, source, maskA);
        });

        int orderedMaskABADrawCount = rig.Draws.Count;
        int maskOnlyTextureSwitches = rig.Batch.FrameStats.TextureSwitches;
        Assert.Equal(3, orderedMaskABADrawCount);
        Assert.Equal(1, maskOnlyTextureSwitches);
        Assert.Equal(new[] { maskA.Handle, maskB.Handle, maskA.Handle },
            rig.Draws.Select(draw => Assert.IsType<FakeResourceSet>(draw.Set0).Resources[2]));
        Assert.Equal(new uint[] { 0, 6, 12 }, rig.Draws.Select(draw => draw.VertexStart));
        Assert.Equal(new uint[] { 0, 384, 768 }, rig.VertexUploads.Select(upload => upload.Offset));
        Assert.All(rig.Draws, draw => Assert.Equal(6u, draw.VertexCount));
        Assert.All(rig.Draws, draw => Assert.Same(rig.VertexUploads[0].Buffer, draw.VertexBuffer));
        Assert.Same(rig.Draws[0].Set0, rig.Draws[2].Set0);
        Assert.Equal(3, rig.Batch.FrameStats.DrawCalls);
        Assert.Equal(1152, rig.Batch.FrameStats.SpriteUploadBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentSourcesSplitEvenWithSameMask(bool group)
    {
        using var rig = Create();
        using var sourceA = Sprite();
        using var sourceB = Sprite();
        using var mask = Sprite();
        rig.Frame(batch => { Masked(batch, sourceA, mask); Masked(batch, sourceB, mask); }, group: group);

        int distinctSourceDrawCount = rig.Draws.Count;
        int distinctSourceTextureSwitches = rig.Batch.FrameStats.TextureSwitches;
        Assert.Equal(2, distinctSourceDrawCount);
        Assert.Equal(2, distinctSourceTextureSwitches);
        Assert.Equal(new[] { sourceA.Handle, sourceB.Handle },
            rig.Draws.Select(draw => Assert.IsType<FakeResourceSet>(draw.Set0).Resources[0]));
    }

    [Fact]
    public void GroupingRetainsMaskBlendAndSamplerKeys()
    {
        using var rig = Create();
        using var source = Sprite();
        using var maskA = Sprite();
        using var maskB = Sprite();
        rig.Frame(batch =>
        {
            Masked(batch, source, maskA);
            Masked(batch, source, maskB);
            Masked(batch, source, maskA);
            batch.BlendMode = BlendMode.Additive;
            Masked(batch, source, maskA);
            Masked(batch, source, maskA, SamplerMode.Point);
        }, group: true);

        Assert.Equal(new uint[] { 12, 6, 6, 6 }, rig.Draws.Select(draw => draw.VertexCount));
        Assert.Equal(new uint[] { 0, 12, 18, 24 }, rig.Draws.Select(draw => draw.VertexStart));
        Assert.Equal(new[] { maskA.Handle, maskB.Handle, maskA.Handle, maskA.Handle },
            rig.Draws.Select(draw => Assert.IsType<FakeResourceSet>(draw.Set0).Resources[2]));
        Assert.Same(rig.Draws[0].Set0, rig.Draws[2].Set0);
        Assert.Same(rig.MaskedOwner!.PipelineFor(BlendMode.Alpha), rig.Draws[0].Pipeline);
        Assert.Same(rig.MaskedOwner.PipelineFor(BlendMode.Additive), rig.Draws[2].Pipeline);
        Assert.Same(rig.Device.PointSampler, Assert.IsType<FakeResourceSet>(rig.Draws[3].Set0).Resources[3]);
        Assert.Equal(1, rig.Batch.FrameStats.TextureSwitches);
        Assert.Equal(1920, rig.Batch.FrameStats.SpriteUploadBytes);
    }

    [Fact]
    public void MixedRunsRestoreOrdinaryBindingsAndUseCurrentVpSlot()
    {
        using var rig = Create();
        using var source = Sprite();
        using var mask = Sprite();
        rig.Frame(batch =>
        {
            batch.Draw(source, new Vector4(10, 20, 30, 40), Color.White);
            Masked(batch, source, mask);
            batch.Draw(source, new Vector4(10, 20, 30, 40), Color.White);
            batch.End();
            batch.Begin(SamplerMode.Point);
            Masked(batch, source, mask);
        });

        Assert.Equal(4, rig.Draws.Count);
        Assert.Equal(new uint[] { 0, 6, 12, 0 }, rig.Draws.Select(draw => draw.VertexStart));
        Assert.Equal(new uint[] { 0, 0, 0, 256 }, rig.Draws.Select(draw => draw.DynamicOffset));
        Assert.All(rig.Draws, draw => Assert.Same(rig.Factory.ResourceSets[0], draw.Set1));
        Assert.Same(rig.Factory.Pipelines[0], rig.Draws[0].Pipeline);
        Assert.Same(rig.Factory.Pipelines[0], rig.Draws[2].Pipeline);
        Assert.Same(rig.Factory.Pipelines[2], rig.Draws[1].Pipeline);
        Assert.Same(rig.Factory.Pipelines[2], rig.Draws[3].Pipeline);
        Assert.Same(rig.Draws[0].Set0, rig.Draws[2].Set0);
        Assert.Equal(2, Assert.IsType<FakeResourceSet>(rig.Draws[2].Set0).Resources.Length);
        Assert.Equal(rig.VertexUploads[0].Data, rig.VertexUploads[2].Data);
        Assert.Same(rig.Device.PointSampler, Assert.IsType<FakeResourceSet>(rig.Draws[3].Set0).Resources[1]);
        Assert.NotSame(rig.Draws[0].VertexBuffer, rig.Draws[3].VertexBuffer);
        Assert.Equal(1, rig.Batch.FrameStats.TextureSwitches);
        Assert.Equal(2, rig.Batch.FrameStats.Flushes);
    }

    [Fact]
    public void CanonicalKeysIncludeBothTexturesBothSamplersAndBlend()
    {
        using var rig = Create();
        var owner = rig.EnsureMaskedResources();
        using var source = Texture();
        using var mask = Texture();
        using var second = Texture();
        var linear = rig.Device.LinearSampler;
        var point = rig.Device.PointSampler;
        var alpha = owner.KeyFor(source, linear, mask, linear, BlendMode.Alpha, 1);
        var again = owner.KeyFor(source, linear, mask, linear, BlendMode.Alpha, 1);
        var additive = owner.KeyFor(source, linear, mask, linear, BlendMode.Additive, 1);
        var otherMask = owner.KeyFor(source, linear, second, linear, BlendMode.Alpha, 1);
        var otherSource = owner.KeyFor(second, linear, mask, linear, BlendMode.Alpha, 1);
        var pointSource = owner.KeyFor(source, point, mask, linear, BlendMode.Alpha, 1);
        var pointMask = owner.KeyFor(source, linear, mask, point, BlendMode.Alpha, 1);

        Assert.Same(alpha, again);
        Assert.NotSame(alpha, additive);
        Assert.NotSame(alpha, otherMask);
        Assert.NotSame(alpha, otherSource);
        Assert.NotSame(alpha, pointSource);
        Assert.NotSame(alpha, pointMask);
        Assert.Same(source, alpha.Source);
        Assert.Equal(BlendMode.Alpha, alpha.Blend);
        Assert.Equal(BlendMode.Additive, additive.Blend);
        Assert.Equal(0, owner.CachedSetCount);
        foreach (var key in new[] { alpha, otherMask, otherSource, pointSource, pointMask }) owner.SetFor(key, 1);
        Assert.Equal(5, owner.CachedSetCount);
        Assert.Collection(rig.Factory.ResourceSets.Skip(1),
            set => Assert.Equal(new IGpuBindableResource[] { source, linear, mask, linear }, set.Resources),
            set => Assert.Equal(new IGpuBindableResource[] { source, linear, second, linear }, set.Resources),
            set => Assert.Equal(new IGpuBindableResource[] { second, linear, mask, linear }, set.Resources),
            set => Assert.Equal(new IGpuBindableResource[] { source, point, mask, linear }, set.Resources),
            set => Assert.Equal(new IGpuBindableResource[] { source, linear, mask, point }, set.Resources));
    }

    [Fact]
    public void BlendVariantsShareOneBindingSet()
    {
        using var rig = Create();
        var owner = rig.EnsureMaskedResources();
        using var source = Texture();
        using var mask = Texture();
        var sampler = rig.Device.LinearSampler;
        var alpha = owner.KeyFor(source, sampler, mask, sampler, BlendMode.Alpha, 1);
        var additive = owner.KeyFor(source, sampler, mask, sampler, BlendMode.Additive, 1);

        Assert.Same(owner.SetFor(alpha, 1), owner.SetFor(additive, 1));
        Assert.Equal(1, owner.CachedSetCount);
        Assert.Same(owner, rig.EnsureMaskedResources());
        Assert.NotSame(owner.PipelineFor(BlendMode.Alpha), owner.PipelineFor(BlendMode.Additive));
        Assert.Equal(2, rig.Factory.ShaderRequests.Count);
        Assert.Equal(4, rig.Factory.GraphicsPipelines.Count);
    }

    [Fact]
    public void ValueEqualDistinctHandlesRemainSeparate()
    {
        using var rig = Create();
        var owner = rig.EnsureMaskedResources();
        using var source = Texture();
        using var mask = Texture();
        using var valueEqualHandleA = new ValueEqualTexture();
        using var valueEqualHandleB = new ValueEqualTexture();
        using var samplerA = new ValueEqualSampler();
        using var samplerB = new ValueEqualSampler();
        var sampler = rig.Device.LinearSampler;
        Assert.NotSame(valueEqualHandleA, valueEqualHandleB);
        Assert.True(valueEqualHandleA.Equals(valueEqualHandleB));
        Assert.True(samplerA.Equals(samplerB));
        var valueEqualMaskKeyA = owner.KeyFor(source, sampler, valueEqualHandleA, sampler, BlendMode.Alpha, 1);
        var valueEqualMaskKeyB = owner.KeyFor(source, sampler, valueEqualHandleB, sampler, BlendMode.Alpha, 1);
        Assert.NotSame(valueEqualMaskKeyA, valueEqualMaskKeyB);
        var keys = new[]
        {
            valueEqualMaskKeyA, valueEqualMaskKeyB,
            owner.KeyFor(valueEqualHandleA, sampler, mask, sampler, BlendMode.Alpha, 1),
            owner.KeyFor(valueEqualHandleB, sampler, mask, sampler, BlendMode.Alpha, 1),
            owner.KeyFor(source, samplerA, mask, sampler, BlendMode.Alpha, 1),
            owner.KeyFor(source, samplerB, mask, sampler, BlendMode.Alpha, 1),
            owner.KeyFor(source, sampler, mask, samplerA, BlendMode.Alpha, 1),
            owner.KeyFor(source, sampler, mask, samplerB, BlendMode.Alpha, 1),
        };
        Assert.Equal(8, keys.Distinct().Count());
        Assert.Equal(8, keys.Select(key => owner.SetFor(key, 1)).Distinct().Count());
        Assert.Equal(8, owner.CachedSetCount);
    }

    [Fact]
    public void UnmaskedOnlyDoesNotCreateMaskedResources()
    {
        using var rig = Create();
        using var handle = Texture();
        using var source = Texture2D.Wrap(handle, 4, 4, ownsHandle: false);
        rig.Frame(batch => batch.Draw(source, new Vector4(2, 3, 8, 9), Color.White));

        var ordinaryFactory = rig.Factory;
        int ordinaryMaskedSetCount = ordinaryFactory.ResourceSets.Count(set => set.Resources.Length == 4);
        Assert.Single(ordinaryFactory.ShaderRequests);
        Assert.Equal(2, ordinaryFactory.GraphicsPipelines.Count);
        Assert.Equal(0, ordinaryMaskedSetCount);
        Assert.Null(rig.MaskedOwner);
        var upload = Assert.Single(rig.VertexUploads);
        var draw = Assert.Single(rig.Draws);
        Assert.Equal(384, upload.Data.Length);
        Assert.Equal(2f, BitConverter.ToSingle(upload.Data, 0));
        Assert.Equal(3f, BitConverter.ToSingle(upload.Data, 4));
        Assert.Same(upload.Buffer, draw.VertexBuffer);
        Assert.Same(handle, Assert.IsType<FakeResourceSet>(draw.Set0).Resources[0]);
        Assert.Same(rig.Factory.ResourceSets[0], draw.Set1);
        Assert.Equal(0u, draw.DynamicOffset);
        Assert.Equal(0u, draw.VertexStart);
        Assert.Equal(6u, draw.VertexCount);
    }

    [Fact]
    public void RecordedDrawsKeepVpOffsetsAndVertexStartsAcrossBegins()
    {
        using var rig = Create();
        using var firstHandle = Texture();
        using var secondHandle = Texture();
        using var first = Texture2D.Wrap(firstHandle, 4, 4, ownsHandle: false);
        using var second = Texture2D.Wrap(secondHandle, 4, 4, ownsHandle: false);
        rig.Frame(batch =>
        {
            batch.Draw(first, new Vector4(0, 0, 4, 4), Color.White);
            batch.Draw(second, new Vector4(4, 0, 4, 4), Color.White);
            batch.End();
            batch.Begin(SamplerMode.Point);
            batch.Draw(first, new Vector4(8, 0, 4, 4), Color.White);
        });

        Assert.Equal(3, rig.VertexUploads.Count);
        Assert.Equal(new uint[] { 0, 6, 0 }, rig.Draws.Select(draw => draw.VertexStart));
        Assert.Equal(new uint[] { 0, 0, 256 }, rig.Draws.Select(draw => draw.DynamicOffset));
        Assert.All(rig.Draws, draw => Assert.Same(rig.Factory.ResourceSets[0], draw.Set1));
        Assert.Same(rig.VertexUploads[0].Buffer, rig.Draws[1].VertexBuffer);
        Assert.Same(rig.VertexUploads[2].Buffer, rig.Draws[2].VertexBuffer);
        Assert.Same(rig.Device.PointSampler, Assert.IsType<FakeResourceSet>(rig.Draws[2].Set0).Resources[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LazyConstructionFailureDoesNotPublishPartialOwner(int failureIndex)
    {
        using var rig = Create();
        using var source = Texture();
        using var mask = Texture();
        using var sprite = Texture2D.Wrap(source, 4, 4, ownsHandle: false);
        var failure = (ConstructionFailure)(failureIndex + 1);
        rig.Forwarder.Failure = failure;
        int layouts = rig.Forwarder.Layouts.Count;
        int shaders = rig.Factory.ShaderSets.Count;
        int pipelines = rig.Factory.Pipelines.Count;

        Assert.Same(rig.Forwarder.CreationFailure, Assert.Throws<InvalidOperationException>(() => rig.EnsureMaskedResources()));
        Assert.Null(rig.MaskedOwner);
        Assert.All(rig.Forwarder.Layouts.Skip(layouts), layout => Assert.True(layout.Disposed));
        Assert.All(rig.Factory.ShaderSets.Skip(shaders), shader => Assert.True(shader.Disposed));
        Assert.All(rig.Factory.Pipelines.Skip(pipelines), pipeline => Assert.True(pipeline.Disposed));
        Assert.False(source.Disposed);
        Assert.False(mask.Disposed);
        Assert.False(Assert.IsType<TrackedSampler>(rig.Device.LinearSampler).Disposed);
        Assert.False(Assert.IsType<TrackedSampler>(rig.Device.PointSampler).Disposed);
        Assert.Equal(0, rig.InnerDevice.WaitForIdleCalls);
        Assert.Equal(0, rig.Forwarder.CommandListRequests);

        rig.Frame(batch => batch.Draw(sprite, new Vector4(0, 0, 4, 4), Color.White));
        Assert.Single(rig.Draws);
        rig.Forwarder.Failure = ConstructionFailure.None;
        Assert.Same(rig.EnsureMaskedResources(), rig.MaskedOwner);
    }

    [Fact]
    public void ConstructionCleanupFailurePreservesOriginalException()
    {
        using var rig = Create();
        var cleanupFailure = new InvalidOperationException("planned masked cleanup failure");
        rig.Forwarder.LayoutDisposalFailure = cleanupFailure;
        rig.Forwarder.Failure = ConstructionFailure.SecondPipeline;

        var error = Assert.Throws<AggregateException>(() => rig.EnsureMaskedResources());
        Assert.Equal(new[] { rig.Forwarder.CreationFailure, cleanupFailure }, error.InnerExceptions);
        Assert.Null(rig.MaskedOwner);
        Assert.True(rig.Factory.ShaderSets[1].Disposed);
        Assert.True(rig.Factory.Pipelines[2].Disposed);
    }

    [Fact]
    public void LazyPipelinesReuseOrdinaryVertexAndOutputState()
    {
        using var rig = Create();
        var owner = rig.EnsureMaskedResources();
        Assert.Equal(3, rig.Forwarder.Layouts.Count);
        Assert.Equal(0, rig.InnerDevice.WaitForIdleCalls);
        Assert.Equal(0, rig.Forwarder.CommandListRequests);
        Assert.Single(rig.Factory.ResourceSets);
        Assert.Equal(SpriteBatch.VertSrc, rig.Factory.ShaderRequests[1].VertexGlsl);
        Assert.Equal(MaskedSpriteShader.FragmentSource, rig.Factory.ShaderRequests[1].FragmentGlsl);
        for (int i = 0; i < 2; i++)
        {
            var ordinary = rig.Factory.GraphicsPipelines[i].Description;
            var masked = rig.Factory.GraphicsPipelines[i + 2].Description;
            Assert.Equal(ordinary.BlendAttachments, masked.BlendAttachments);
            Assert.Equal(ordinary.BlendFactor, masked.BlendFactor);
            Assert.Equal(ordinary.DepthStencil, masked.DepthStencil);
            Assert.Equal(ordinary.Rasterizer, masked.Rasterizer);
            Assert.Equal(ordinary.Topology, masked.Topology);
            Assert.Equal(ordinary.Outputs, masked.Outputs);
            Assert.Equal(ordinary.VertexLayouts[0], masked.VertexLayouts[0]);
            Assert.Same(ordinary.VertexLayouts[0].Elements, masked.VertexLayouts[0].Elements);
            Assert.Same(ordinary.ResourceLayouts[1], masked.ResourceLayouts[1]);
            Assert.Same(rig.Forwarder.Layouts[2], masked.ResourceLayouts[0]);
        }
        Assert.Same(rig.Factory.Pipelines[2], owner.PipelineFor(BlendMode.Alpha));
        Assert.Same(rig.Factory.Pipelines[3], owner.PipelineFor(BlendMode.Additive));
    }

    [Fact]
    public void MaskedOwnerDisposesAfterDrainBeforeSharedViewResources()
    {
        using var rig = Create();
        var owner = rig.EnsureMaskedResources();
        using var source = Texture();
        using var mask = Texture();
        var sampler = rig.Device.LinearSampler;
        var set = Assert.IsType<FakeResourceSet>(owner.SetFor(owner.KeyFor(source, sampler, mask, sampler, BlendMode.Alpha, 1), 1));
        rig.InnerDevice.OnWaitForIdle = () =>
        {
            Assert.False(set.Disposed);
            Assert.False(rig.Forwarder.Layouts[2].Disposed);
        };
        rig.Forwarder.Layouts[2].OnDispose = () =>
        {
            Assert.Equal(1, rig.InnerDevice.WaitForIdleCalls);
            Assert.False(rig.Forwarder.Layouts[1].Disposed);
            Assert.False(rig.Factory.Buffers[0].Disposed);
            Assert.False(rig.Factory.ResourceSets[0].Disposed);
        };

        rig.Batch.Dispose();
        Assert.True(set.Disposed);
        Assert.All(rig.Factory.Pipelines.Skip(2), pipeline => Assert.True(pipeline.Disposed));
        Assert.True(rig.Factory.ShaderSets[1].Disposed);
        Assert.False(source.Disposed);
        Assert.False(mask.Disposed);
        Assert.False(Assert.IsType<TrackedSampler>(sampler).Disposed);
    }

    sealed class ValueEqualTexture : IGpuTexture
    {
        public uint Width => 4;
        public uint Height => 4;
        public uint MipLevels => 1;
        public uint SampleCount => 1;
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8UNorm;
        public override bool Equals(object? obj) => obj is ValueEqualTexture;
        public override int GetHashCode() => 1;
        public void Dispose() { }
    }

    sealed class ValueEqualSampler : IGpuSampler
    {
        public override bool Equals(object? obj) => obj is ValueEqualSampler;
        public override int GetHashCode() => 1;
        public void Dispose() { }
    }
}
