using System;
using System.Globalization;
using System.Numerics;
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

    [Fact]
    public void TheShaderTreatsTheSameValuesAsBackgroundAsEveryOtherConsumer()
        => Assert.Contains(MotionMath.BackgroundThreshold.ToString("0.0", CultureInfo.InvariantCulture),
            ShaderSources.MotionVectorsViewFrag, StringComparison.Ordinal);
}
