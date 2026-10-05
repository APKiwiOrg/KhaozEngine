using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// JITTER NEVER REACHES THE CPU (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, risk 2 and acceptance 3). Two
/// identical scenes render the same still frames, one with temporal rendering forced on, so its jitter moves every
/// frame. Nothing the CPU derives from the view may move with it: the fitted cascades, the set of instances the main
/// pass keeps, the water planes the water pass routes, and the shadow depth pass's decision to skip. A jittered cascade
/// fit would re-render the shadow atlas every frame, which is the regression this exists to catch.
/// <para>
/// An instance within half a pixel of the frustum edge is what a jittered cull would drop, and no fixed scene can
/// promise one sits there, so the culled-set comparison is the end-to-end half. The wiring half is the sweep row that
/// pins <c>FrustumPlanes.Extract(absVp)</c> to the absolute snapshot (FrameViewConsumerSweepTests). Water planes are
/// placed from the camera instead: a row of them crosses the left edge of the view a tenth of a pixel apart, so a
/// jittered water cull moves the edge through the row and changes a plane's route.
/// </para>
/// </summary>
public sealed class TemporalCpuIsolationTests
{
    const int Frames = 6;

    /// <summary>Water planes in the row across the left edge, a tenth of a pixel apart from two pixels outside it to two
    /// inside, which covers the jitter's half internal pixel at every preset down to Performance.</summary>
    const int RowPlanes = 41;
    const float RowY = 0.4f;

    sealed class StillScene : IDisposable
    {
        internal readonly HeadlessSceneRig Rig = new();
        readonly MeshHandle _floor, _box;

        internal StillScene(bool temporal, bool perspective)
        {
            Scene3D scene = Rig.Scene;
            scene.ForceTemporalForTests = temporal;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowNearDistance = 5f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
            if (perspective)
                scene.CameraOverride = new FollowCamera3D
                {
                    Target = new Vector3(0.2f, 0.4f, 0f),
                    Yaw = 0.7f,
                    Pitch = 0.5f,
                    Distance = 9f,
                    AspectRatio = (float)HeadlessSceneRig.Width / HeadlessSceneRig.Height,
                };
            scene.Post.Water.SwellAmplitude = 0f;   // no reach, so each plane's box is its own tiny rectangle
            _floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            _box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
        }

        internal Scene3D Scene => Rig.Scene;

        /// <summary>The water planes the next frame queues.</summary>
        internal WaterPlane[] Water = Array.Empty<WaterPlane>();

        /// <summary>The camera the scene renders through, read back to check the snapshot against its own matrices.</summary>
        internal IIsoCamera3D Camera => Scene.CameraOverride ?? Scene.Camera;

        internal void Frame() => Rig.Frame(s =>
        {
            s.Draw(_floor, Matrix4x4.Identity);
            s.Draw(_box, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            s.Draw(_box, Matrix4x4.CreateTranslation(1.1f, 0.7f, 0.9f));
            s.Draw(_box, Matrix4x4.CreateTranslation(0f, 300f, 0f));   // far above every view: the culled member
            foreach (WaterPlane plane in Water) s.DrawWater(plane);
        });

        public void Dispose() => Rig.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JitterNeverReachesTheCascadeFitTheCulledSetOrTheShadowSkip(bool perspective)
    {
        using var off = new StillScene(temporal: false, perspective);
        using var on = new StillScene(temporal: true, perspective);
        var jitters = new HashSet<Vector2>();

        for (int frame = 0; frame < Frames; frame++)
        {
            off.Water = on.Water = WaterRowAcrossTheLeftEdge(off.Camera);
            off.Frame();
            on.Frame();
            Scene3D a = off.Scene, b = on.Scene;

            Assert.Equal(Vector2.Zero, a.CurrentFrameView.JitterPixels);
            Assert.NotEqual(Vector2.Zero, b.CurrentFrameView.JitterPixels);
            jitters.Add(b.CurrentFrameView.JitterPixels);

            // The snapshot-level half: with the jitter moving, the forced scene's unjittered slots still hold its
            // camera's own matrices bit for bit. Every CPU path below reads one of them.
            IIsoCamera3D camera = on.Camera;
            TemporalAssert.BitIdentical(camera.ViewProjection, b.CurrentFrameView.ViewProjection,
                $"frame {frame} temporal ViewProjection against the camera's");
            TemporalAssert.BitIdentical(((IRenderOriginAware)camera).AbsoluteViewProjection,
                b.CurrentFrameView.AbsoluteViewProjection, $"frame {frame} temporal AbsoluteViewProjection against the camera's");

            TemporalAssert.BitIdentical(a.CurrentFrameView.ViewProjection, b.CurrentFrameView.ViewProjection,
                $"frame {frame} ViewProjection");
            TemporalAssert.BitIdentical(a.CurrentFrameView.AbsoluteViewProjection, b.CurrentFrameView.AbsoluteViewProjection,
                $"frame {frame} AbsoluteViewProjection");

            AssertSameFit(a.CascadeFitAbsoluteForTests, b.CascadeFitAbsoluteForTests, $"frame {frame} absolute cascade");
            AssertSameFit(a.CascadeFitRelativeForTests, b.CascadeFitRelativeForTests, $"frame {frame} relative cascade");

            Assert.Equal(a.MainPassVisibilityForTests.ToArray(), b.MainPassVisibilityForTests.ToArray());
            Assert.Equal((a.DrawnInstances, a.CulledInstances), (b.DrawnInstances, b.CulledInstances));

            for (int i = 0; i < RowPlanes; i++)
                Assert.True(a.WaterRouteForTests(i) == b.WaterRouteForTests(i),
                    $"frame {frame}: the jittered scene routed water plane {i} of the row {b.WaterRouteForTests(i)}, "
                    + $"the still one {a.WaterRouteForTests(i)}");

            Assert.Equal(a.ShadowPassSkippedLastFrame, b.ShadowPassSkippedLastFrame);
            Assert.Equal(frame > 0, b.ShadowPassSkippedLastFrame);
            Assert.False(b.LastShadowPassDiagnostics.LightMatrixChanged,
                $"frame {frame}: the jittered scene's cascade matrices moved");
        }

        Assert.True(jitters.Count == Frames,
            $"the forced scene took {jitters.Count} distinct jitters over {Frames} frames, so the jitter did not move");
        Assert.True(off.Scene.CulledInstances > 0, "nothing was culled, so the culled-set comparison proves nothing");
        Assert.True(off.Scene.CascadeFitAbsoluteForTests.Length > 0, "no cascade was fitted, so the fit comparison proves nothing");
        int culledWater = 0;
        for (int i = 0; i < RowPlanes; i++)
            if (off.Scene.WaterRouteForTests(i) == KhaozEngine.Render3D.Rendering.WaterRenderer.PlaneRoute.Culled) culledWater++;
        Assert.True(culledWater > 0 && culledWater < RowPlanes,
            $"{culledWater} of {RowPlanes} water planes were culled, so the row does not cross the edge of the view");
        // Temporal rendering owns the motion target and its pipelines, so the two scenes' factory counts differ by
        // design (MotionTargetWiringTests pins that lifecycle).
    }

    /// <summary>
    /// The row of water planes across the left edge of <paramref name="camera"/>'s unjittered view, at the height of its
    /// centre. Each plane sits where the ray through that point of the screen meets <see cref="RowY"/>.
    /// </summary>
    static WaterPlane[] WaterRowAcrossTheLeftEdge(IIsoCamera3D camera)
    {
        Assert.True(Matrix4x4.Invert(((IRenderOriginAware)camera).AbsoluteViewProjection, out Matrix4x4 inverse));
        var row = new WaterPlane[RowPlanes];
        for (int i = 0; i < RowPlanes; i++)
        {
            float ndcX = -1f + (i - RowPlanes / 2) * 0.1f * 2f / HeadlessSceneRig.Width;
            Vector3 near = Unproject(new Vector3(ndcX, 0f, 0f), inverse), far = Unproject(new Vector3(ndcX, 0f, 1f), inverse);
            Vector3 point = near + (far - near) * ((RowY - near.Y) / (far.Y - near.Y));
            row[i] = new WaterPlane(point.X, RowY, point.Z, 0.001f);
        }
        return row;
    }

    static Vector3 Unproject(Vector3 ndc, in Matrix4x4 inverse)
    {
        Vector4 h = Vector4.Transform(new Vector4(ndc, 1f), inverse);
        return new Vector3(h.X, h.Y, h.Z) / h.W;
    }

    static void AssertSameFit(ReadOnlySpan<Matrix4x4> expected, ReadOnlySpan<Matrix4x4> actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) TemporalAssert.BitIdentical(expected[i], actual[i], $"{what} {i}");
    }
}
