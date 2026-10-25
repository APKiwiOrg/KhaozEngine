using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using static KhaozEngine.Tests.Render2D.SpriteBatchMaskedTestRig;

namespace KhaozEngine.Tests.Render2D;

public class SpriteBatchMaskedBatchTests
{
    static FakeTexture Texture() => new(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);

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
