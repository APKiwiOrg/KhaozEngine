using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>What the model renderer's skinned draw paths bind under the base and the temporal model target, read off a
/// recording list over a fake device. The CPU-skinned facts pin the fix for the defect D5 left behind: a temporal
/// target made the rigid pipeline the rigid motion variant, and CPU-skinned draws kept binding it with neither its set
/// 1 nor its vertex slot 2.</summary>
public sealed class ModelMotionBindTests
{
    // The model-pass sources these facts can meet, by name, so a failure reads as a program name and not as GLSL.
    static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [ShaderSources.ModelVert] = nameof(ShaderSources.ModelVert),
        [ShaderSources.ModelFrag] = nameof(ShaderSources.ModelFrag),
        [ShaderSources.ModelDissolveFrag] = nameof(ShaderSources.ModelDissolveFrag),
        [ShaderSources.ModelMotionVert] = nameof(ShaderSources.ModelMotionVert),
        [ShaderSources.ModelMotionFrag] = nameof(ShaderSources.ModelMotionFrag),
        [ShaderSources.ModelCpuSkinnedMotionVert] = nameof(ShaderSources.ModelCpuSkinnedMotionVert),
        [ShaderSources.ModelDissolveMotionFrag] = nameof(ShaderSources.ModelDissolveMotionFrag),
        [ShaderSources.SkinnedModelMotionVert] = nameof(ShaderSources.SkinnedModelMotionVert),
        [ShaderSources.SkinnedModelMotionFrag] = nameof(ShaderSources.SkinnedModelMotionFrag),
    };

    // The program a recorded draw's pipeline was built from, as "vertex + fragment".
    static string Program(IGpuPipeline? pipeline)
    {
        FakeGraphicsPipelineRequest request = Assert.IsType<FakePipeline>(pipeline).Request!.Value;
        return Names.GetValueOrDefault(request.VertexGlsl, "another vertex") + " + "
            + Names.GetValueOrDefault(request.FragmentGlsl, "another fragment");
    }

    // The CPU-skinned pass in the order Scene3D records it: the non-dissolving pipeline and a draw, then the dissolve
    // pipeline and a draw.
    static IReadOnlyList<RecordingGpuCommandList.DrawBindings> RecordCpuSkinnedPass(FakeGpuDevice device,
        ModelRenderer model)
    {
        var uploads = new NullGpuCommandList();
        model.UploadCpuSkinned(uploads, new ModelVertex[3], new ModelRenderer.InstanceData[1]);
        if (model.Motion is not null) model.UploadCpuSkinnedPrevious(uploads, new Vector3[3]);
        using IGpuBuffer ib = device.Factory.CreateBuffer(new GpuBufferDescription(6, GpuBufferUsage.IndexBuffer));
        var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true };

        model.BindCpuSkinnedPass(cl);
        model.DrawCpuSkinned(cl, ib, 3, GpuIndexFormat.UInt16, 0, 0, null);
        model.BindDissolvePass(cl);
        model.DrawCpuSkinned(cl, ib, 3, GpuIndexFormat.UInt16, 0, 0, null);
        return cl.Bindings;
    }

    [Fact]
    public void UnderTheBaseTargetCpuSkinnedDrawsBindThePreTemporalPipelinesAndNothingAboveThem()
    {
        using var device = new FakeGpuDevice();
        using var model = new ModelRenderer(device, ModelTargets.Base, 64, 1);

        IReadOnlyList<RecordingGpuCommandList.DrawBindings> draws = RecordCpuSkinnedPass(device, model);

        Assert.Equal(2, draws.Count);
        Assert.Equal("ModelVert + ModelFrag", Program(draws[0].Pipeline));
        Assert.Equal("ModelVert + ModelDissolveFrag", Program(draws[1].Pipeline));
        Assert.All(draws, draw =>
        {
            Assert.Equal(new uint[] { 0 }, draw.Sets.Keys.Order().ToArray());
            Assert.Equal(new uint[] { 0, 1 }, draw.VertexBuffers.Keys.Order().ToArray());
            Assert.Same(model.CpuSkinnedVertexBuffer, draw.VertexBuffers[0]);
            Assert.Same(model.CpuSkinnedInstanceBuffer, draw.VertexBuffers[1]);
        });
    }

    [Fact]
    public void UnderTheTemporalTargetCpuSkinnedDrawsBindTheirVariantsTheMotionBlockAndLastFramesPositions()
    {
        using var device = new FakeGpuDevice();
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);
        ModelMotionResources motion = model.Motion!;

        IReadOnlyList<RecordingGpuCommandList.DrawBindings> draws = RecordCpuSkinnedPass(device, model);

        Assert.Equal(2, draws.Count);
        Assert.Equal("ModelCpuSkinnedMotionVert + ModelMotionFrag", Program(draws[0].Pipeline));
        Assert.Equal("ModelCpuSkinnedMotionVert + ModelDissolveMotionFrag", Program(draws[1].Pipeline));
        Assert.All(draws, draw =>
        {
            Assert.Equal(new uint[] { 0, 1 }, draw.Sets.Keys.Order().ToArray());
            Assert.Same(motion.FrameSet, draw.Sets[1].Set);
            Assert.Equal(new uint[] { 0, 1, 2 }, draw.VertexBuffers.Keys.Order().ToArray());
            Assert.Same(model.CpuSkinnedVertexBuffer, draw.VertexBuffers[0]);
            Assert.Same(model.CpuSkinnedInstanceBuffer, draw.VertexBuffers[1]);
            Assert.Same(motion.CpuPrevious, draw.VertexBuffers[2]);
        });
    }

    [Fact]
    public void AGpuSkinnedDrawBindsItsCastersLastFrameAtSetThreeAtThatCastersSlot()
    {
        using var device = new FakeGpuDevice();
        using var model = new ModelRenderer(device, ModelTargets.Temporal, 64, 1);
        ModelMotionResources motion = model.Motion!;
        model.EnsureSkinnedMainCapacity(4);
        model.EnsureSkinnedBonePaletteCapacity(4);
        model.EnsureSkinnedMotionCapacity(4);
        using IGpuBuffer vb = device.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer ib = device.Factory.CreateBuffer(new GpuBufferDescription(6, GpuBufferUsage.IndexBuffer));
        var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true };

        // Scene3D passes one compacted caster index as both slots. They differ here, so the fact also pins that set 3
        // follows the palette slot, as set 2 does.
        model.BindSkinnedPass(cl);
        model.DrawGpuSkinned(cl, vb, ib, 3, GpuIndexFormat.UInt16, slot: 1, paletteSlot: 2, null);

        RecordingGpuCommandList.DrawBindings draw = Assert.Single(cl.Bindings);
        Assert.Equal("SkinnedModelMotionVert + SkinnedModelMotionFrag", Program(draw.Pipeline));
        Assert.Same(motion.SkinnedPalette.Set, draw.Sets[3].Set);
        Assert.Equal(SkinnedMotionPalette.OffsetFor(2), draw.Sets[3].DynamicOffset);
        Assert.Equal(2u * 8448u, draw.Sets[3].DynamicOffset);
    }
}
