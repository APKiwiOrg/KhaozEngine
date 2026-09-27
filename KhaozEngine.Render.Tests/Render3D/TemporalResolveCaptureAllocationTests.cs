using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The headless half of <c>TemporalResolveCaptureAllocationGpuTests</c>: every frame resolves the main view
    /// and then renders a capture from another camera at the same size, with bloom on, and a steady frame allocates
    /// nothing, set rebuilds included.</summary>
    [Collection("AllocSensitive")]
    public sealed class TemporalResolveCaptureAllocationTests
    {
        [Fact]
        public void A_steady_resolving_frame_with_a_capture_every_frame_allocates_nothing()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Bloom.Enabled = true;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
            var other = new FlyCamera3D
            {
                Position = new Vector3(4f, 3f, 6f), Yaw = 3.6f, Pitch = -0.35f, AspectRatio = 240f / 160f,
            };
            Action<Scene3D> draw = s =>
            {
                s.Draw(box, Matrix4x4.CreateTranslation(0f, 0.5f, 0f));
                s.DrawBeam(new Vector3(-4f, 1f, 0f), new Vector3(4f, 1f, 0f), 0.3f, Color.White);
            };
            void Frame()
            {
                rig.Frame(240, 160, draw);
                scene.CameraOverride = other;
                rig.Render(240, 160);
                scene.CameraOverride = null;
            }

            for (int i = 0; i < 8; i++) Frame();
            Assert.True(scene.LaterRenderPostCreatedForTests && scene.BloomAllocated);
            AllocAssert.NoPerCallAllocation("16 steady resolving frames with a capture each", () =>
            {
                for (int i = 0; i < 16; i++) Frame();
            });
            Assert.True(scene.LastTemporalDiagnostics.HistoryValid);
        }
    }
}
