using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// <see cref="FollowCamera3D.FrameClock"/>, the per-frame id that lets a camera read in the update step and
    /// again through the render pay one eye computation a frame
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1189">#1189</see>).
    /// <para>
    /// A frame here is what a <c>GameApp</c> frame does to the camera: the clock ticks first, then the update moves
    /// the target and may read the eye, then <c>Scene3D.Begin</c> latches the camera (<see cref="FollowCamera3D.BeginFrame"/>
    /// followed by an <see cref="FollowCamera3D.Eye"/> read) and the render reads it dozens more times. The rows pin
    /// the saving AND the staleness bound, because the obvious way to get the first breaks the second: a latch that
    /// keeps whatever was computed since the last latch keeps the render's own eye from the frame before.
    /// </para>
    /// </summary>
    public class FollowCamera3DFrameClockTests
    {
        /// <summary>Where the update step reads the camera from, relative to moving it.</summary>
        public enum Shape
        {
            /// <summary>Moves the target in update and reads only through the render (the showcase).</summary>
            RenderOnly,
            /// <summary>Reads in update, then moves the target (a hover pick before the camera advances).</summary>
            ReadBeforeMove,
            /// <summary>Moves the target, then reads in update (a listener, steering, a pick), then renders.</summary>
            ReadAfterMove,
            /// <summary>As <see cref="ReadAfterMove"/> with a target that never moves.</summary>
            ReadStandingStill,
        }

        /// <summary>The framing the eye-cache rows use: looking down -Z at the origin from 10 m.</summary>
        static FollowCamera3D Camera(ICameraBoomProbe? probe, Func<long>? clock)
        {
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, BoomProbe = probe, FrameClock = clock,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            return cam;
        }

        /// <summary>What an update step reads: the listener's eye and forward, and a cursor pick.</summary>
        static void UpdateReads(FollowCamera3D cam)
        {
            _ = cam.Eye;
            _ = cam.Forward;
            _ = cam.ScreenToRay(new Vector2(400f, 300f), 800, 600);
        }

        /// <summary>What <c>Scene3D</c> does with its active camera: <c>LatchRenderOrigin</c> calls
        /// <c>BeginFrame</c> and reads <c>Eye</c>, then the passes and the HUD read every derived path.</summary>
        static void Render(FollowCamera3D cam)
        {
            cam.BeginFrame();
            _ = cam.Eye;
            for (int i = 0; i < 4; i++)
            {
                _ = cam.Forward;
                _ = cam.View;
                _ = cam.ViewProjection;
                _ = cam.AbsoluteViewProjection;
                cam.WorldToScreen(Vector3.Zero, 800, 600, out _);
                _ = cam.ScreenToRay(new Vector2(400f, 300f), 800, 600);
                _ = cam.ScreenToGround(new Vector2(400f, 300f), 800, 600);
            }
        }

        static void RunFrame(FollowCamera3D cam, Shape shape, int frame)
        {
            Vector3 target = shape == Shape.ReadStandingStill ? Vector3.Zero : new Vector3(frame, 0f, 0f);
            switch (shape)
            {
                case Shape.RenderOnly:
                    cam.Target = target;
                    break;
                case Shape.ReadBeforeMove:
                    UpdateReads(cam);
                    cam.Target = target;
                    break;
                default:
                    cam.Target = target;
                    UpdateReads(cam);
                    break;
            }
            Render(cam);
        }

        /// <summary>Runs one warm frame, then <paramref name="frames"/> measured ones, ticking
        /// <paramref name="tick"/> at the top of each exactly as <c>GameApp</c> ticks its clock before
        /// <c>OnUpdate</c>. Returns the eye computations and probe calls per measured frame.</summary>
        static (double Computes, double ProbeCalls) PerFrame(FollowCamera3D cam, FixedReachProbe probe, Shape shape,
            Action tick, int frames = 10)
        {
            tick();
            RunFrame(cam, shape, 0);
            long computes = cam.EyeComputeCount;
            int calls = probe.Calls;
            for (int f = 1; f <= frames; f++)
            {
                tick();
                RunFrame(cam, shape, f);
            }
            return ((cam.EyeComputeCount - computes) / (double)frames, (probe.Calls - calls) / (double)frames);
        }

        [Theory]
        [InlineData(Shape.ReadAfterMove)]
        [InlineData(Shape.ReadStandingStill)]
        public void With_a_clock_an_update_read_after_the_move_and_the_render_share_one_compute(Shape shape)
        {
            var probe = new FixedReachProbe { ReachAt = 6f };
            long frame = 0;
            FollowCamera3D cam = Camera(probe, () => frame);

            (double computes, double calls) = PerFrame(cam, probe, shape, () => frame++);

            Assert.Equal(1.0, computes);
            Assert.Equal(1.0, calls);
            Assert.Equal(cam.EyeComputeCount, cam.BoomProbeCount);   // the camera's own counter agrees
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_render_only_camera_computes_once_a_frame_with_or_without_a_clock(bool clocked)
        {
            var probe = new FixedReachProbe { ReachAt = 6f };
            long frame = 0;
            FollowCamera3D cam = Camera(probe, clocked ? () => frame : null);

            (double computes, double calls) = PerFrame(cam, probe, Shape.RenderOnly, () => frame++);

            Assert.Equal(1.0, computes);
            Assert.Equal(1.0, calls);
        }

        [Theory]
        [InlineData(Shape.RenderOnly, 1.0)]
        [InlineData(Shape.ReadBeforeMove, 1.0)]
        [InlineData(Shape.ReadAfterMove, 2.0)]   // the #1189 cost, which the clock is the opt-in cure for
        [InlineData(Shape.ReadStandingStill, 1.0)]
        public void Without_a_clock_every_shape_keeps_the_numbers_it_had(Shape shape, double expected)
        {
            var probe = new FixedReachProbe { ReachAt = 6f };
            FollowCamera3D cam = Camera(probe, null);

            (double computes, double calls) = PerFrame(cam, probe, shape, () => { });

            Assert.Equal(expected, computes);
            Assert.Equal(expected, calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_still_camera_recomputes_once_a_frame_while_the_world_behind_its_probe_moves(bool readsInUpdate)
        {
            // The known trap. Nothing about the camera changes from frame to frame: only the obstruction behind the
            // probe moves, which no cache key on a camera field can see. A latch that kept the eye because one was
            // computed since the last latch would keep the render's eye from the frame before and never see it.
            var probe = new FixedReachProbe();
            long frame = 0;
            FollowCamera3D cam = Camera(probe, () => frame);

            float[] walls = { 9f, 8f, 7f, 6f, 5f };
            for (int i = 0; i < walls.Length; i++)
            {
                probe.ReachAt = walls[i];   // the wall moves between frames
                frame++;                    // the host ticks the clock at the top of the frame
                Vector3 fromUpdate = readsInUpdate ? cam.Eye : default;
                Render(cam);

                float boom = Vector3.Distance(cam.Eye, cam.Pivot);
                Assert.Equal(walls[i] - cam.OcclusionSkin, boom, 4);
                if (readsInUpdate) Assert.Equal(fromUpdate, cam.Eye);   // the render reused the update's eye
                Assert.Equal(i + 1L, cam.EyeComputeCount);              // exactly one a frame, never zero
                Assert.Equal(i + 1, probe.Calls);
            }
        }

        [Fact]
        public void A_still_camera_with_a_clock_sees_a_physics_wall_slide_in_on_the_next_frame()
        {
            using var world = new CountingPhysicsWorld();   // frame 1: a clear boom
            long frame = 1;
            FollowCamera3D cam = Camera(null, () => frame);
            cam.Occlusion = world;

            Render(cam);
            Assert.Equal(10f, Vector3.Distance(cam.Eye, cam.Pivot), 3);

            world.WallDistance = 6.25f;   // a wall slides in between the frames
            frame++;
            Render(cam);

            float boom = Vector3.Distance(cam.Eye, cam.Pivot);
            Assert.Equal(6.25f - cam.OcclusionRadius - cam.OcclusionSkin, boom, 3);
            Assert.Equal(2, world.SweepCount);
        }

        [Fact]
        public void A_stalled_clock_falls_back_to_the_scene_latch()
        {
            // A clock that is wired but never ticked gives the camera no frame boundary of its own. The scene latch
            // must then behave exactly as it does for a camera with no clock: from the second latch on, drop the
            // cache every time, so the render still sees a wall that slid in under a camera that did not move.
            var stalledProbe = new FixedReachProbe();
            var bareProbe = new FixedReachProbe();
            FollowCamera3D stalled = Camera(stalledProbe, () => 7L);
            FollowCamera3D bare = Camera(bareProbe, null);

            UpdateReads(stalled);
            Render(stalled);
            UpdateReads(bare);
            Render(bare);
            long stalledWarm = stalled.EyeComputeCount, bareWarm = bare.EyeComputeCount;

            for (int f = 1; f <= 6; f++)
            {
                float? wall = f % 2 == 0 ? 6f : null;   // in and out, and no camera input changes
                stalledProbe.ReachAt = wall;
                bareProbe.ReachAt = wall;

                Vector3 stalledUpdate = stalled.Eye;
                UpdateReads(stalled);
                Render(stalled);
                Vector3 bareUpdate = bare.Eye;
                UpdateReads(bare);
                Render(bare);

                Assert.Equal(bareUpdate, stalledUpdate);
                Assert.Equal(bare.Eye, stalled.Eye);
                Assert.Equal(wall is { } w ? w - stalled.OcclusionSkin : 10f, Vector3.Distance(stalled.Eye, stalled.Pivot), 4);
                Assert.Equal(bare.EyeComputeCount - bareWarm, stalled.EyeComputeCount - stalledWarm);
            }
        }

        [Fact]
        public void The_clock_advancing_between_two_reads_recomputes()
        {
            var probe = new FixedReachProbe();
            long frame = 1;
            FollowCamera3D cam = Camera(probe, () => frame);

            _ = cam.Eye;
            _ = cam.Eye;
            Assert.Equal(1L, cam.EyeComputeCount);

            probe.ReachAt = 6f;
            frame++;   // a new frame with no latch in between: the stamp alone expires the eye

            Assert.Equal(6f - cam.OcclusionSkin, Vector3.Distance(cam.Eye, cam.Pivot), 4);
            Assert.Equal(2L, cam.EyeComputeCount);
        }

        [Fact]
        public void A_second_latch_in_the_same_clock_frame_drops_the_cache()
        {
            // Two scenes rendering one camera, or a consumer latching beside the scene: the clock did not move
            // between the two latches, so the second one is treated as a stall and costs a recompute, the same
            // as a camera with no clock. Cost, never staleness.
            var probe = new FixedReachProbe();
            long frame = 1;
            FollowCamera3D cam = Camera(probe, () => frame);

            Render(cam);
            Render(cam);

            Assert.Equal(2L, cam.EyeComputeCount);
        }

        [Fact]
        public void InvalidateEye_still_forces_a_recompute_under_a_clock()
        {
            var probe = new FixedReachProbe();
            long frame = 1;
            FollowCamera3D cam = Camera(probe, () => frame);

            Render(cam);
            probe.ReachAt = 6f;
            cam.InvalidateEye();   // the consumer moved an occluder mid-frame

            Assert.Equal(6f - cam.OcclusionSkin, Vector3.Distance(cam.Eye, cam.Pivot), 4);
            Assert.Equal(2L, cam.EyeComputeCount);
        }

        /// <summary>
        /// The wiring row: <c>Scene3D.Begin</c> latches a clocked camera without dropping the eye the update
        /// computed earlier in the same frame, and still drops it once the clock moves on. It needs a device only
        /// because <c>Scene3D</c>'s constructor does.
        /// </summary>
        [GpuFact]
        public void Scene3D_Begin_keeps_this_frames_eye_and_drops_the_previous_frames()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);

            var probe = new FixedReachProbe();
            long frame = 1;
            FollowCamera3D cam = Camera(probe, () => frame);
            scene.CameraOverride = cam;

            Vector3 fromUpdate = cam.Eye;   // the update's read, before the scene begins
            scene.Begin();
            Assert.Equal(1L, cam.EyeComputeCount);
            Assert.Equal(fromUpdate, cam.Eye);

            probe.ReachAt = 6f;             // the world moves between the scene's frames
            frame++;
            scene.Begin();

            Assert.Equal(2L, cam.EyeComputeCount);
            Assert.Equal(6f - cam.OcclusionSkin, Vector3.Distance(cam.Eye, cam.Pivot), 4);
        }
    }
}
