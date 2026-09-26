using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The foliage variant's prepare-time half, reachable now that a temporal frame allocates the motion target:
/// when a slot keeps the last frame its submission folded in and when it folds its own, which pixel scale it measures
/// against, and the motion block the draw binds.</summary>
public sealed class MotionTargetFoliageWiringTests
{
    sealed class Rig : IDisposable
    {
        static readonly FoliageRenderSettings Settings = new() { DistantDensity = 1f };
        readonly MotionTestScene _harness = new();
        readonly FoliageBatch _batch;

        internal Rig()
        {
            MeshHandle blade = Scene.LoadMesh(MeshPrimitives.Box(1f));
            _batch = Scene.CreateFoliageBatch(new[] { new FoliageInstance(blade, Matrix4x4.Identity, .1f) });
            Scene.ForceTemporalForTests = true;
        }

        internal Scene3D Scene => _harness.Scene;

        /// <summary>This frame's one foliage slot, as the last render uploaded it.</summary>
        internal FoliageUniforms Slot => Scene.FoliageUniformsForTests[0];

        internal void Begin(float orthoSize)
        {
            Scene.Camera.OrthoSize = orthoSize;
            Scene.Begin();
        }

        internal void Draw(float focusX, float seconds)
        {
            Scene.EffectTimeSeconds = seconds;
            Assert.True(Scene.DrawFoliage(_batch, new Vector3(focusX, 0f, 4f), Settings) > 0);
        }

        internal RecordingGpuCommandList Render()
        {
            Scene.PrepareFrame();
            var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true };
            Scene.RenderInternal(cl, MotionTestScene.Width, MotionTestScene.Height, _harness.Target);
            return cl;
        }

        internal void Frame(float orthoSize, float focusX, float seconds)
        {
            Begin(orthoSize);
            Draw(focusX, seconds);
            Render().Dispose();
        }

        public void Dispose()
        {
            _batch.Dispose();
            _harness.Dispose();
        }
    }

    // A slot whose last frame is its own: the recomputed displacement cancels and the variant writes camera-only motion.
    static void AssertSelfFolded(FoliageUniforms slot)
    {
        Assert.Equal(slot.WithPrevious(slot), slot);
        Assert.Equal(slot.WindFade.Y, slot.WindFade.Z);
    }

    [Fact]
    public void WithoutValidHistoryEverySlotFoldsItsOwnStateScaleIncluded()
    {
        using var rig = new Rig();
        rig.Frame(orthoSize: 6f, focusX: 3f, seconds: 1f);
        rig.Frame(orthoSize: 6f, focusX: 3.5f, seconds: 1.5f);
        Assert.NotEqual(rig.Slot.WithPrevious(rig.Slot), rig.Slot);   // valid history: the batch's last frame

        rig.Scene.CameraCut();
        rig.Frame(orthoSize: 8f, focusX: 4f, seconds: 2f);   // the submission folded last frame in, then the cut
        Assert.Null(rig.Scene.PreviousFrameView);
        AssertSelfFolded(rig.Slot);
    }

    [Fact]
    public void WithValidHistoryASlotMeasuresAgainstLastFramesStateAndPixelScale()
    {
        using var rig = new Rig();
        rig.Frame(orthoSize: 6f, focusX: 3f, seconds: 1f);
        float lastScale = rig.Slot.WindFade.Y;

        rig.Frame(orthoSize: 9f, focusX: 3.5f, seconds: 1.5f);
        Assert.NotNull(rig.Scene.PreviousFrameView);
        Assert.Equal(new Vector4(3f, 0f, 4f, 1f), rig.Slot.PrevFocus);
        Assert.Equal(lastScale, rig.Slot.WindFade.Z);
        Assert.NotEqual(lastScale, rig.Slot.WindFade.Y);
    }

    [Fact]
    public void ASecondRenderInsideTheFrameKeepsLastFramesPixelScale()
    {
        using var rig = new Rig();
        rig.Frame(orthoSize: 6f, focusX: 3f, seconds: 1f);
        float lastScale = rig.Slot.WindFade.Y;

        rig.Begin(orthoSize: 9f);
        rig.Draw(focusX: 3.5f, seconds: 1.5f);
        rig.Render().Dispose();
        float scale = rig.Slot.WindFade.Y;
        rig.Render().Dispose();   // an offscreen capture inside the same frame

        Assert.Equal((scale, lastScale), (rig.Slot.WindFade.Y, rig.Slot.WindFade.Z));
    }

    [Fact]
    public void ABatchSubmittedBeforeTemporalTurnsOnInTheFrameFoldsItsOwnState()
    {
        using var rig = new Rig();
        rig.Frame(orthoSize: 6f, focusX: 3f, seconds: 1f);

        rig.Scene.ForceTemporalForTests = false;
        rig.Begin(orthoSize: 9f);
        rig.Draw(focusX: 3.5f, seconds: 1.5f);   // submitted with temporal off: its slot carries no last frame
        rig.Scene.ForceTemporalForTests = true;  // on again before the frame's first render
        rig.Render().Dispose();

        Assert.NotNull(rig.Scene.PreviousFrameView);   // history is valid, so only the late enable forces the fold
        AssertSelfFolded(rig.Slot);
    }

    static RecordingGpuCommandList.DrawBindings FoliageDraw(RecordingGpuCommandList cl) => Assert.Single(cl.Bindings,
        d => d.Pipeline is FakePipeline { Request: { } r }
            && (r.VertexGlsl == ShaderSources.FoliageVert || r.VertexGlsl == ShaderSources.FoliageMotionVert));

    [Fact]
    public void TheFoliageDrawBindsItsVariantAndTheMotionBlockAtSetTwoOnlyWhileTemporal()
    {
        using var rig = new Rig();
        rig.Scene.ForceTemporalForTests = false;
        rig.Begin(orthoSize: 6f);
        rig.Draw(focusX: 3f, seconds: 1f);
        using (RecordingGpuCommandList off = rig.Render())
        {
            RecordingGpuCommandList.DrawBindings draw = FoliageDraw(off);
            Assert.Equal(ShaderSources.FoliageVert, ((FakePipeline)draw.Pipeline!).Request!.Value.VertexGlsl);
            Assert.Equal(new uint[] { 0, 1 }, draw.Sets.Keys.Order().ToArray());
        }

        rig.Scene.ForceTemporalForTests = true;
        rig.Begin(orthoSize: 6f);
        rig.Draw(focusX: 3f, seconds: 1.5f);
        using (RecordingGpuCommandList on = rig.Render())
        {
            RecordingGpuCommandList.DrawBindings draw = FoliageDraw(on);
            FakeGraphicsPipelineRequest request = ((FakePipeline)draw.Pipeline!).Request!.Value;
            Assert.Equal((ShaderSources.FoliageMotionVert, ShaderSources.ModelMotionFrag), (request.VertexGlsl, request.FragmentGlsl));
            Assert.Equal(new uint[] { 0, 1, 2 }, draw.Sets.Keys.Order().ToArray());
            Assert.Same(rig.Scene.MotionResourcesForTests!.FrameSet, draw.Sets[2].Set);
        }
    }
}
