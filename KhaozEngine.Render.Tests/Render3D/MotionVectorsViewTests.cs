using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class MotionVectorsViewTests
{
    [Fact]
    public void TheViewIsBuiltOnlyWhenSelectedAndDrawsIntoTheFinalTarget()
    {
        using var harness = new MotionTestScene();
        var factory = (FakeGpuResourceFactory)harness.Device.Factory;
        MeshHandle box = harness.Scene.LoadMesh(MeshPrimitives.Box(1f));

        harness.Scene.ForceTemporalForTests = true;
        harness.Scene.Begin();
        harness.Scene.Draw(box, Matrix4x4.Identity);
        harness.Render();
        Assert.False(harness.Scene.MotionVectorsViewBuiltForTests);
        harness.Scene.ForceTemporalForTests = false;

        harness.Scene.DebugView = SceneDebugView.MotionVectors;
        harness.Scene.Begin();
        harness.Scene.Draw(box, Matrix4x4.Identity);
        harness.Render();

        Assert.True(harness.Scene.MotionVectorsViewBuiltForTests);
        FakeGraphicsPipelineRequest view = Assert.Single(factory.GraphicsPipelines,
            p => p.FragmentGlsl == ShaderSources.MotionVectorsViewFrag);
        Assert.Equal(harness.Target.Outputs.Colour, view.Description.Outputs.Colour);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AViewSelectedAfterTheFramesFirstRenderWaitsForTheNextFrame(bool temporalFrame)
    {
        // The frame fixes its debug view at its first render, beside its temporal state. A frame that was not temporal
        // has no motion target to sample, and a temporal one keeps drawing what its first render chose.
        using var harness = new MotionTestScene();
        MeshHandle box = harness.Scene.LoadMesh(MeshPrimitives.Box(1f));
        harness.Scene.ForceTemporalForTests = temporalFrame;
        harness.Scene.Begin();
        harness.Scene.Draw(box, Matrix4x4.Identity);
        harness.Render();

        harness.Scene.DebugView = SceneDebugView.MotionVectors;
        Assert.Null(Record.Exception(harness.Render));   // a second render of the same frame
        Assert.False(harness.Scene.MotionVectorsViewBuiltForTests);

        harness.Scene.Begin();
        harness.Scene.Draw(box, Matrix4x4.Identity);
        harness.Render();
        Assert.True(harness.Scene.MotionVectorsViewBuiltForTests);
    }

    /// <summary>A frame that does not draw the view drops it, so its resource set never outlives the motion target it
    /// samples, whether temporal rendering turns off with it or stays on for another requester.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFrameWithoutTheViewDropsIt(bool temporalStaysOn)
    {
        using var harness = new MotionTestScene();
        var factory = (FakeGpuResourceFactory)harness.Device.Factory;
        MeshHandle box = harness.Scene.LoadMesh(MeshPrimitives.Box(1f));
        void Frame()
        {
            harness.Scene.Begin();
            harness.Scene.Draw(box, Matrix4x4.Identity);
            harness.Render();
        }

        harness.Scene.ForceTemporalForTests = temporalStaysOn;
        harness.Scene.DebugView = SceneDebugView.MotionVectors;
        Frame();
        Frame();
        Assert.True(harness.Scene.MotionVectorsViewBuiltForTests);
        FakeTexture motion = Assert.Single(factory.Textures, t => !t.Disposed && t.Format == MotionMath.Format
            && factory.ResourceSets.Any(s => s.Resources.Contains(t)));
        FakeResourceSet[] viewSets = factory.ResourceSets.Where(s => s.Resources.Contains(motion)).ToArray();
        Assert.NotEmpty(viewSets);

        harness.Scene.DebugView = SceneDebugView.None;
        Frame();
        Assert.False(harness.Scene.MotionVectorsViewBuiltForTests);
        Assert.Equal(!temporalStaysOn, motion.Disposed);
        // The view goes to the scene's retire queue, because the last frame's commands may still read it.
        for (int i = 0; i <= GpuRetireQueue.DefaultFrameDelay; i++) Frame();
        Assert.All(viewSets, s => Assert.True(s.Disposed));
    }

    /// <summary>The view's background test is <see cref="MotionMath.IsBackground"/>, the rule the resolve uses too: the
    /// U channel alone past the threshold. A pixel whose V alone is large is motion, and the view draws it.</summary>
    [Fact]
    public void TheShaderTreatsTheSameValuesAsBackgroundAsEveryOtherConsumer()
    {
        string[] lines = ShaderSources.MotionVectorsViewFrag.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Contains("const float BackgroundThreshold = "
            + MotionMath.BackgroundThreshold.ToString("0.0", CultureInfo.InvariantCulture) + ";", lines);
        string test = Assert.Single(lines, line => line.Contains("> BackgroundThreshold", StringComparison.Ordinal));
        Assert.Equal("    if (abs(motion.x) > BackgroundThreshold || magnitude < StillPixels) {", test);

        // The C# rule the line restates.
        Assert.True(MotionMath.IsBackground(new Vector2(MotionMath.BackgroundThreshold + 1f, 0f)));
        Assert.True(MotionMath.IsBackground(new Vector2(-MotionMath.BackgroundThreshold - 1f, 0f)));
        Assert.False(MotionMath.IsBackground(new Vector2(0f, MotionMath.Sentinel)));
        Assert.False(MotionMath.IsBackground(new Vector2(MotionMath.BackgroundThreshold, 0f)));
    }
}
