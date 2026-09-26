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
/// pass keeps, and the shadow depth pass's decision to skip. A jittered cascade fit would re-render the shadow atlas
/// every frame, which is the regression this exists to catch.
/// <para>
/// An instance within half a pixel of the frustum edge is what a jittered cull would drop, and no fixed scene can
/// promise one sits there, so the culled-set comparison is the end-to-end half. The wiring half is the sweep row that
/// pins <c>FrustumPlanes.Extract(absVp)</c> to the absolute snapshot (FrameViewConsumerSweepTests).
/// </para>
/// </summary>
public sealed class TemporalCpuIsolationTests
{
    const int Frames = 6;

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
                    Target = new Vector3(0.2f, 0.4f, 0f), Yaw = 0.7f, Pitch = 0.5f, Distance = 9f,
                    AspectRatio = (float)HeadlessSceneRig.Width / HeadlessSceneRig.Height,
                };
            _floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            _box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
        }

        internal Scene3D Scene => Rig.Scene;

        /// <summary>The camera the scene renders through, read back to check the snapshot against its own matrices.</summary>
        internal IIsoCamera3D Camera => Scene.CameraOverride ?? Scene.Camera;

        internal void Frame() => Rig.Frame(s =>
        {
            s.Draw(_floor, Matrix4x4.Identity);
            s.Draw(_box, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            s.Draw(_box, Matrix4x4.CreateTranslation(1.1f, 0.7f, 0.9f));
            s.Draw(_box, Matrix4x4.CreateTranslation(0f, 300f, 0f));   // far above every view: the culled member
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

            Assert.Equal(a.ShadowPassSkippedLastFrame, b.ShadowPassSkippedLastFrame);
            Assert.Equal(frame > 0, b.ShadowPassSkippedLastFrame);
            Assert.False(b.LastShadowPassDiagnostics.LightMatrixChanged,
                $"frame {frame}: the jittered scene's cascade matrices moved");
        }

        Assert.True(jitters.Count == Frames,
            $"the forced scene took {jitters.Count} distinct jitters over {Frames} frames, so the jitter did not move");
        Assert.True(off.Scene.CulledInstances > 0, "nothing was culled, so the culled-set comparison proves nothing");
        Assert.True(off.Scene.CascadeFitAbsoluteForTests.Length > 0, "no cascade was fitted, so the fit comparison proves nothing");
        // Temporal rendering owns the motion target and its pipelines, so the two scenes' factory counts differ by
        // design (MotionTargetWiringTests pins that lifecycle).
    }

    static void AssertSameFit(ReadOnlySpan<Matrix4x4> expected, ReadOnlySpan<Matrix4x4> actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) TemporalAssert.BitIdentical(expected[i], actual[i], $"{what} {i}");
    }
}
