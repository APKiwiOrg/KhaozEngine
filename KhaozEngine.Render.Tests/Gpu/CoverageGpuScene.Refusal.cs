using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageGpuScene
{
    internal void AssertArithmeticRefusal()
    {
        var actual = ArithmeticRefusalCapture(control: false);
        Dispose();
        using var fresh = new CoverageGpuScene();
        var expected = fresh.ArithmeticRefusalCapture(control: true);
        CoveragePixelProof.EqualTarget(actual.Depth, expected.Depth, CoveragePixelProof.DepthTolerance, "refusal depth");
        CoveragePixelProof.EqualTarget(actual.Normal, expected.Normal, CoveragePixelProof.NormalTolerance, "refusal normal");
        CoveragePixelProof.EqualTarget(actual.Cascades!, expected.Cascades!, CoveragePixelProof.DepthTolerance, "refusal cascades");
        CoveragePixelProof.EqualTarget(actual.TransientPoints!, expected.TransientPoints!, CoveragePixelProof.DepthTolerance, "refusal point faces");
        CoveragePixelProof.EqualTarget(actual.FullOutline!, expected.FullOutline!, 0, "refusal full outline");
        CoveragePixelProof.EqualTarget(actual.VisibleOutline!, expected.VisibleOutline!, CoveragePixelProof.NormalTolerance, "refusal visible outline");
        CoveragePixelProof.AssertNoGhostColour(actual, expected);
    }

    CoverageCapture ArithmeticRefusalCapture(bool control)
    {
        var row = new CoverageCase(0) { Shadows = true, StaticLight = true, Outlines = true };
        SetCase(row, true);
        PrimePoints(row);
        var scene = Fixture.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        var actor = Actors[0];
        void Draw(Scene3D current, int unused)
        {
            current.AddLight(Vector3.Zero, Color.White, 6, 1, LightShadow.Static(8001));
            actor.Draw(row, control ? 0 : 1, Matrix4x4.Identity);
        }
        Fixture.Frame(Draw);
        if (!control) actor.AssertCovered();
        scene.RenderOrigin = new(128, 0, 0);
        byte[] colour = Fixture.Frame(Draw);
        if (!control)
        {
            var outcome = scene.GetCoverageOutcome(actor.Frame);
            Assert.Equal(CoveragePresentation.UncoveredFallback, outcome.Presentation);
            Assert.Equal(CoverageFailureCode.UnsupportedTransform, outcome.Failure.Code);
            AssertRejectedConfidence();
            CoverageTemporalCurrentProbe.AssertRejectedPixelsMatchCurrent(this);
        }
        var motion = scene.ReadMotionTargetForTests();
        Assert.Contains(motion.Motion, v => !MotionMath.IsBackground(v));
        Assert.All(motion.Motion.Where(v => !MotionMath.IsBackground(v)),
            v => Assert.InRange(v.Length(), 0, .000001f));
        var outlines = scene.ReadCoverageOutlinesForTests();
        var capture = new CoverageCapture(colour, scene.ReadCoverageDepthForTests(), scene.ReadCoverageNormalForTests(),
            scene.ReadCoverageCascadesForTests(), scene.ReadCoveragePointsForTests(false), scene.ReadCoveragePointsForTests(true),
            outlines.Full, outlines.Visible, "uncovered-current-pose", Fixture.Device.Backend,
            Fixture.Device.Capabilities.DeviceName, $"coverage-{captureId}-{captureNumber++}-arithmetic-refusal-control{control}")
        { Motion = motion, Jitter = scene.CurrentFrameView.JitterPixels, Temporal = scene.LastTemporalDiagnostics, FrameStats = scene.LastFrameStats, ReconstructionColour = scene.ReadCoverageReconstructionColourForTests() };
        CoverageArtifacts.Write(capture);
        return capture;
    }
}
