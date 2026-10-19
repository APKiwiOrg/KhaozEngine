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
        var actual = ArithmeticRefusalCapture(CoverageStream.Accumulated);
        Dispose();
        CoverageCapture expected;
        CoverageHostFootprint before, after;
        using (var fresh = new CoverageGpuScene()) expected = fresh.ArithmeticRefusalCapture(CoverageStream.CurrentOnly);
        using (var control = new CoverageGpuScene()) before = control.ArithmeticRefusalFootprint(CoverageStream.HostBefore);
        using (var control = new CoverageGpuScene()) after = control.ArithmeticRefusalFootprint(CoverageStream.HostAfter);
        CoveragePixelProof.EqualTarget(actual.Depth, expected.Depth, CoveragePixelProof.DepthTolerance, "refusal depth");
        CoveragePixelProof.EqualTarget(actual.Normal, expected.Normal, CoveragePixelProof.NormalTolerance, "refusal normal");
        CoveragePixelProof.EqualTarget(actual.Cascades!, expected.Cascades!, CoveragePixelProof.DepthTolerance, "refusal cascades");
        CoveragePixelProof.EqualTarget(actual.TransientPoints!, expected.TransientPoints!, CoveragePixelProof.DepthTolerance, "refusal point faces");
        CoveragePixelProof.EqualTarget(actual.FullOutline!, expected.FullOutline!, 0, "refusal full outline");
        CoveragePixelProof.EqualTarget(actual.VisibleOutline!, expected.VisibleOutline!, CoveragePixelProof.NormalTolerance, "refusal visible outline");
        AssertCurrentColour(actual, expected, requireHost: true, requirePlate: true);
        AssertMaskComplete(before, after, actual, hostChange: true, negative: false);
    }

    // The control presents the uncovered current pose throughout. The host-only controls draw the conformed surface
    // and the uncovered host at the refused frame.
    void DriveArithmeticRefusal(CoverageStream stream, Action<byte[]> observe)
    {
        var row = new CoverageCase(0) { Shadows = true, StaticLight = true, Outlines = true };
        SetCase(row, true);
        PrimePoints(row);
        var scene = Fixture.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        var actor = Actors[0];
        int route = stream switch { CoverageStream.Accumulated => 1, CoverageStream.HostBefore => 2, _ => 0 };
        bool equipment = stream is CoverageStream.Accumulated or CoverageStream.CurrentOnly;
        bool refusedFrame = false;
        void Draw(Scene3D current, int unused)
        {
            current.AddLight(Vector3.Zero, Color.White, 6, 1, LightShadow.Static(8001));
            actor.Draw(row, route, Matrix4x4.Identity, submitEquipment: equipment,
                recession: stream == CoverageStream.Accumulated && refusedFrame ? .0001f : null);
        }
        Fixture.Frame(Draw);
        if (stream == CoverageStream.Accumulated) actor.AssertCovered();
        refusedFrame = true;
        scene.RenderOrigin = new(128, 0, 0);
        if (stream == CoverageStream.CurrentOnly) scene.CameraCut();
        observe(Fixture.Frame(Draw));
    }

    CoverageHostFootprint ArithmeticRefusalFootprint(CoverageStream stream)
    {
        CoverageHostFootprint? footprint = null;
        DriveArithmeticRefusal(stream, _ => footprint = HostFootprint($"coverage-{captureId}-arithmetic-refusal-{stream}"));
        return footprint!;
    }

    CoverageCapture ArithmeticRefusalCapture(CoverageStream stream)
    {
        CoverageCapture? capture = null;
        var scene = Fixture.Scene;
        DriveArithmeticRefusal(stream, colour =>
        {
            bool accumulated = stream == CoverageStream.Accumulated;
            if (accumulated)
            {
                var actor = Actors[0];
                Assert.Equal(CoveragePresentation.Dispatched, scene.GetCoverageOutcome(actor.Frame).Presentation);
                var words = CoverageNativeSurface.Read(Fixture.Device, scene, actor.Frame);
                uint offset = actor.Frame.Preparation!.GpuReservation.CurrentBase;
                Assert.Equal((uint)CoverageFailureCode.RecessionExceeded, words[offset + 8]);
                Assert.Equal(1u, words[offset + 14]);
                AssertRejectedConfidence();
                CoverageTemporalCurrentProbe.AssertRejectedPixelsMatchCurrent(this);
            }
            var motion = scene.ReadMotionTargetForTests();
            Assert.Contains(motion.Motion, v => !MotionMath.IsBackground(v));
            Assert.All(motion.Motion.Where(v => !MotionMath.IsBackground(v)),
                v => Assert.InRange(v.Length(), 0, .000001f));
            capture = TemporalCapture(colour, "uncovered-current-pose", accumulated,
                $"coverage-{captureId}-{captureNumber++}-arithmetic-refusal-control{!accumulated}");
            CoverageArtifacts.Write(capture);
        });
        return capture!;
    }
}
