using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The resolve's lifetime and sizes in a scene: nothing exists until temporal anti-aliasing is selected, the history is
    /// display-sized and the previous depth internal-sized, the display chain follows the display, and everything is
    /// released when the mode is left. Headless, on <see cref="HeadlessSceneRig"/>.
    /// </summary>
    public sealed class TemporalResolveWiringTests
    {
        [Fact]
        public void Nothing_temporal_is_created_while_the_resolve_is_off()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.ForceTemporalForTests = true;   // temporal rendering on, the resolve off
            for (int i = 0; i < 3; i++) rig.Frame(96, 64);
            Assert.Null(rig.Scene.TemporalResolveRendererForTests);
            Assert.Null(rig.Scene.TemporalPostTargetsForTests);
            Assert.False(rig.Scene.TemporalHistory.TargetsAllocated);
        }

        [Fact]
        public void The_resolve_allocates_at_the_display_and_internal_sizes_and_releases_when_left()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            rig.Frame(240, 160);
            rig.Frame(240, 160);

            TemporalHistory history = rig.Scene.TemporalHistory;
            Assert.Equal((160, 107), (rig.Scene.RenderTargetWidth, rig.Scene.RenderTargetHeight));
            Assert.Equal((240u, 160u), (history.Color(0).Width, history.Color(0).Height));
            Assert.Equal((240u, 160u), (history.Confidence(1).Width, history.Confidence(1).Height));
            Assert.Equal((160u, 107u), (history.PreviousDepth(0).Width, history.PreviousDepth(0).Height));
            TemporalPostTargets post = rig.Scene.TemporalPostTargetsForTests!;
            Assert.Equal((240, 160), (post.Width, post.Height));
            Assert.Equal((160u, 107u), (post.OpaqueColor.Width, post.OpaqueColor.Height));
            Assert.Equal(1f, rig.Scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
            Assert.Equal(new System.Numerics.Vector4(160f, 107f, 240f, 160f),
                rig.Scene.TemporalResolveRendererForTests.LastUniforms.Sizes);

            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            rig.Frame(240, 160);
            Assert.False(history.TargetsAllocated);
            Assert.False(post.Allocated);
        }

        [Fact]
        public void The_first_frame_and_a_display_resize_resolve_without_history()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(120, 120);
            Assert.Equal(0f, rig.Scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
            rig.Frame(120, 120);
            rig.Frame(120, 120);
            Assert.Equal(1f, rig.Scene.TemporalResolveRendererForTests.LastUniforms.Jitter.W);

            rig.Frame(128, 120);
            Assert.Equal(0f, rig.Scene.TemporalResolveRendererForTests.LastUniforms.Jitter.W);
            rig.Frame(128, 120);
            Assert.Equal(1f, rig.Scene.TemporalResolveRendererForTests.LastUniforms.Jitter.W);
        }

        [Fact]
        public void A_second_render_in_the_same_frame_does_not_advance_the_history_pair()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            int read = rig.Scene.TemporalHistory.ReadIndex;
            rig.Render(96, 64);
            Assert.Equal(read, rig.Scene.TemporalHistory.ReadIndex);
            rig.Frame(96, 64);
            Assert.Equal(1 - read, rig.Scene.TemporalHistory.ReadIndex);
        }

        /// <summary>A later render inside the frame from another camera at the same size pairs its own view with the
        /// previous view the frame's first render latched, so it must not read history. It never resolves: the resolve's
        /// uniforms, the pair and the targets stay the main render's. It renders unjittered and presents through the
        /// internal chain on a post chain of its own, and adds that chain's bloom and ping pairs in place, without the
        /// drain a recreation of the internal targets would take. The next frame keeps all of it.</summary>
        [Fact]
        public void A_later_render_from_another_camera_at_the_same_size_never_resolves()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Bloom.Enabled = true;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            Assert.True(scene.ResolvedLastRenderForTests);
            Assert.False(scene.InternalPingsAllocatedForTests);
            Assert.False(scene.BloomAllocated);
            Assert.False(scene.LaterRenderPostCreatedForTests);
            TemporalResolveUniforms main = scene.TemporalResolveRendererForTests!.LastUniforms;
            Assert.Equal(1f, main.Jitter.W);
            int read = scene.TemporalHistory.ReadIndex, generation = scene.TemporalHistory.TargetGeneration;

            scene.CameraOverride = new FlyCamera3D
            {
                Position = new Vector3(6f, 3f, 9f), Yaw = 0.7f, Pitch = -0.3f, AspectRatio = 96f / 64f,
            };
            int drains = rig.Device.WaitForIdleCalls;
            rig.Render(96, 64);   // a capture from another camera inside the same frame

            Assert.False(scene.ResolvedLastRenderForTests);
            Assert.Equal(Vector2.Zero, scene.CurrentFrameView.JitterPixels);
            Assert.Equal(main, scene.TemporalResolveRendererForTests!.LastUniforms);
            Assert.Equal((read, generation), (scene.TemporalHistory.ReadIndex, scene.TemporalHistory.TargetGeneration));
            Assert.True(scene.InternalPingsAllocatedForTests, "the capture had no internal chain to present through");
            Assert.True(scene.BloomAllocated, "the capture's internal chain has no bloom pair");
            Assert.True(scene.LaterRenderPostCreatedForTests);
            Assert.Equal(drains, rig.Device.WaitForIdleCalls);   // added in place, nothing the first render reads was freed

            scene.CameraOverride = null;
            rig.Frame(96, 64);
            Assert.True(scene.ResolvedLastRenderForTests);
            Assert.True(scene.LastTemporalDiagnostics.HistoryValid, "the capture reset the main view's history");
            Assert.Equal(1f, scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
            Assert.True(scene.InternalPingsAllocatedForTests && scene.BloomAllocated && scene.LaterRenderPostCreatedForTests,
                "a host that captures every frame would reallocate the internal chain every frame");

            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;   // the internal chain is _post's again
            rig.Frame(96, 64);
            Assert.False(scene.LaterRenderPostCreatedForTests);
            Assert.True(scene.InternalPingsAllocatedForTests && scene.BloomAllocated);
        }

        /// <summary>The resolve's sets name only its four inputs and the history targets, so the distortion field coming
        /// and going, which moves the scene targets' generation, rebuilds none of them.</summary>
        [Fact]
        public void A_distortion_field_coming_and_going_keeps_the_resolve_sets()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            Action<Scene3D> ripple = s => s.DrawDistortion(new DistortionSprite
            {
                Position = new Vector3(0f, 1f, 0f), Size = 1f, Strength = 0.5f,
            });
            for (int i = 0; i < 3; i++) rig.Frame(96, 64);
            TemporalResolveRenderer renderer = scene.TemporalResolveRendererForTests!;
            IGpuResourceSet before = renderer.CurrentSet!;

            rig.Frame(96, 64, ripple);
            Assert.True(scene.TemporalPostTargetsForTests!.DistortAllocated);
            IGpuResourceSet during = renderer.CurrentSet!;
            rig.Frame(96, 64);
            Assert.False(scene.TemporalPostTargetsForTests!.DistortAllocated);

            Assert.Same(before, renderer.CurrentSet);   // the same read index two frames on, through the field's life
            rig.Frame(96, 64);
            Assert.Same(during, renderer.CurrentSet);
        }

        /// <summary>
        /// The resolve reprojects through the same previous view the motion target does: the last frame's first render
        /// rebased to this frame's origin. Across a 128 m origin step and a camera move, the UV the resolve gives a still
        /// point last frame (its reprojection and previous projection) matches the UV the motion block's previous
        /// view-projection gives it, within 0.05 internal pixels plus a 1024th of the motion. A resolve reading the
        /// previous view unrebased would miss by the whole step.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_resolve_and_the_motion_target_reproject_through_the_same_rebased_previous_view(bool perspective)
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            var fly = new FlyCamera3D
            {
                Position = new Vector3(60f, 6f, 30f), Yaw = 0.4f, Pitch = -0.35f, AspectRatio = 240f / 160f,
                NearPlane = 0.1f, FarPlane = 400f,
            };
            if (perspective) scene.CameraOverride = fly;
            scene.Camera.Target = new Vector3(70.5f, 0f, 10.25f);
            scene.RenderOrigin = Vector3.Zero;
            rig.Frame(240, 160);
            scene.RenderOrigin = new Vector3(128f, 0f, 0f);
            scene.Camera.Target += new Vector3(0.6f, 0f, -0.3f);
            fly.Position += new Vector3(0.4f, 0.05f, -0.25f);
            fly.Yaw += 0.01f;
            rig.Frame(240, 160);

            FrameView current = scene.CurrentFrameView;
            FrameView previous = TemporalAssert.Previous(scene);   // what the motion block's PrevViewProj is read from
            TemporalResolveUniforms uniforms = scene.TemporalResolveRendererForTests!.LastUniforms;
            Assert.Equal(1f, uniforms.Jitter.W);
            Assert.Equal(new Vector3(128f, 0f, 0f), previous.RenderOrigin);
            var size = new Vector2(current.Width, current.Height);
            Vector3 focus = perspective ? fly.Position + fly.Forward * 12f : scene.Camera.Target;
            foreach (Vector3 offset in new[]
            {
                Vector3.Zero, new Vector3(3.5f, 0.5f, -4f), new Vector3(-4.25f, 1.5f, 4.5f), new Vector3(2.25f, 0f, 2.25f),
            })
            {
                Vector3 local = focus + offset - current.RenderOrigin;
                Vector4 clip = Vector4.Transform(new Vector4(local, 1f), current.ViewProjection);
                var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                float linearDepth = -Vector3.Transform(local, current.View).Z;
                Vector2? resolved = TemporalResolveMath.StaticPreviousUv(uniforms, ndc, linearDepth);
                Assert.True(resolved.HasValue, $"the resolve put {offset} behind last frame's camera");
                Vector2 motionUv = TemporalAssert.Uv(local, previous.ViewProjection);
                Vector2 motionPixels = (TemporalAssert.Uv(local, current.ViewProjection) - motionUv) * size;
                float gap = ((resolved.Value - motionUv) * size).Length();
                float tolerance = 0.05f + motionPixels.Length() / 1024f;
                Assert.True(gap <= tolerance,
                    $"at {offset} the resolve and the motion target disagree by {gap} px, past {tolerance} px");
            }
        }

        /// <summary>History targets replaced where the frame key cannot see it are new and hold nothing, so the resolve
        /// reads no history that frame and the frame's diagnostics say so.</summary>
        [Fact]
        public void History_targets_replaced_behind_the_frame_key_are_never_read_as_history()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            for (int i = 0; i < 3; i++) rig.Frame(96, 64);
            Assert.Equal(1f, scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);

            scene.TemporalHistory.ReleaseTargets();
            rig.Frame(96, 64);

            Assert.Equal(0f, scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
            Assert.False(scene.LastTemporalDiagnostics.HistoryValid);
            Assert.Equal(TemporalResetReason.Resize, scene.LastTemporalDiagnostics.LastReset);
            Assert.Null(scene.PreviousFrameView);
            rig.Frame(96, 64);
            Assert.Equal(1f, scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
        }

        /// <summary>A resize and the frame that leaves the resolve both hand the history targets to the scene's retire
        /// queue, because the last frame's commands may still read them. Leaving also lets go of the resolve's sets over
        /// them and keeps its pipelines for a restart.</summary>
        [Fact]
        public void Replaced_or_released_history_targets_are_retired_not_freed_in_place()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(96, 64);
            var first = (FakeTexture)scene.TemporalHistory.Color(0);
            rig.Frame(128, 64);
            Assert.False(first.Disposed, "a resize freed the history in place");

            var second = (FakeTexture)scene.TemporalHistory.Color(0);
            TemporalResolveRenderer renderer = scene.TemporalResolveRendererForTests!;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            rig.Frame(128, 64);
            Assert.False(scene.TemporalHistory.TargetsAllocated);
            Assert.False(second.Disposed, "leaving the resolve freed the history in place");
            Assert.Same(renderer, scene.TemporalResolveRendererForTests);   // the pipelines stay for the next time
            Assert.False(renderer.HoldsSetsForTests, "the resolve kept sets over the released history");

            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(128, 64);
            Assert.Same(renderer, scene.TemporalResolveRendererForTests);
            Assert.True(renderer.HoldsSetsForTests);
        }

        /// <summary>The display chain reads its own bloom and ping pair, so under the resolve the internal targets carry
        /// neither. Leaving the resolve brings both back.</summary>
        [Fact]
        public void Under_the_resolve_the_internal_targets_carry_no_bloom_or_ping_pair()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Bloom.Enabled = true;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            rig.Frame(240, 160);
            rig.Frame(240, 160);

            Assert.False(scene.BloomAllocated);
            Assert.False(scene.InternalPingsAllocatedForTests);
            TemporalPostTargets post = scene.TemporalPostTargetsForTests!;
            Assert.True(post.BloomAllocated);
            Assert.Equal((120, 80), (post.BloomWidth, post.BloomHeight));

            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            rig.Frame(240, 160);
            Assert.True(scene.BloomAllocated);
            Assert.True(scene.InternalPingsAllocatedForTests);
        }

        [Fact]
        public void The_opaque_copy_refuses_scene_targets_it_was_not_sized_for()
        {
            using var device = new FakeGpuDevice();
            using var res = new RenderResources(device, 64, 36, hdrColor: true);
            var history = new TemporalHistory();
            using var post = new TemporalPostTargets(device);
            using IGpuCommandList cl = device.Factory.CreateCommandList();
            history.EnsureTargets(device, 96, 54, 64, 36);
            post.Ensure(res, history, 96, 54, bloomEnabled: false);
            cl.Begin();
            post.CopyOpaque(cl);

            res.Resize(80, 45, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true);

            Assert.Throws<InvalidOperationException>(() => post.CopyOpaque(cl));
            cl.End();
            history.ReleaseTargets();
        }
    }
}
