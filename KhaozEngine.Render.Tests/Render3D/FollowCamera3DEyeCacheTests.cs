using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The follow camera's per-frame eye cache
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/28">#28</see>). <c>Eye</c> used to run its
    /// occlusion sweep and its ground sample on EVERY property read, and <c>Forward</c>, <c>View</c>,
    /// <c>ViewProjection</c>, <c>AbsoluteViewProjection</c>, <c>WorldToScreen</c>, <c>ScreenToRay</c> and
    /// <c>ScreenToGround</c> all funnel back through it, so one <c>Scene3D.Render</c> issued dozens of broadphase
    /// sweeps where one would do.
    /// <para>
    /// The rows below pin both halves of the fix, because the obvious way to get the first one is to break the
    /// second: the cache must collapse a frame's reads onto one computation AND must never answer a later frame,
    /// or a mid-frame knob change, with a value that is no longer true.
    /// </para>
    /// </summary>
    public class FollowCamera3DEyeCacheTests
    {
        /// <summary>
        /// An <see cref="IPhysicsWorld"/> that counts sweeps and reports a wall the test can move, so a row can
        /// slide an occluder in between frames without building geometry. Everything the camera never calls throws,
        /// so a future camera change that starts querying something else shows up here rather than being silently
        /// absorbed.
        /// </summary>
        sealed class CountingPhysicsWorld : IPhysicsWorld
        {
            /// <summary>Sweeps issued against this world, counted on the world's own side so a row can prove the
            /// saving without trusting the camera's own counter.</summary>
            public int SweepCount;

            /// <summary>Distance from the sweep start to the wall's surface, or null for a clear boom. Assigning it
            /// is this fake's "a wall slid in", the move no camera field can see.</summary>
            public float? WallDistance;

            /// <summary>The probe stops its own radius short of the surface, which is what a real sphere sweep
            /// reports and what makes <c>OcclusionRadius</c> observable in the returned eye.</summary>
            public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
                out SweepHit hit, QueryFilter filter = default)
            {
                SweepCount++;
                if (WallDistance is { } wall)
                {
                    float d = wall - capsule.Radius;
                    if (d >= 0f && d <= maxDistance)
                    {
                        hit = new SweepHit(d, pose.Position + direction * d, -direction, null);
                        return true;
                    }
                }
                hit = default;
                return false;
            }

            public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
                => throw new NotSupportedException();
            public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
            public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
                PhysicsMaterial? material = null) => throw new NotSupportedException();
            public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
            public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
            public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
                => throw new NotSupportedException();
            public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
                => throw new NotSupportedException();
            public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
            public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
            public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
            public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
            public void Step(float dt) => throw new NotSupportedException();
            public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
                QueryFilter filter = default) => throw new NotSupportedException();
            public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
                => throw new NotSupportedException();
            public void Dispose() { }
        }

        /// <summary>A camera looking straight down +Z at the origin from 10 m, the framing every existing
        /// FollowCamera3D occlusion row uses, so the geometry stays comparable across the two files.</summary>
        static FollowCamera3D Camera(IPhysicsWorld? occlusion)
        {
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, Occlusion = occlusion,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            return cam;
        }

        /// <summary>Where the boom ends up for a wall this far from the target: the probe stops its radius short of
        /// the surface, then the skin backs it off further.</summary>
        static float BoomAfterPullIn(FollowCamera3D cam, float wallDistance)
            => wallDistance - cam.OcclusionRadius - cam.OcclusionSkin;

        /// <summary>Every read path that funnels back through <c>Eye</c>, once each. Eight per call, so four calls
        /// plus one bare Eye is the 33 re-entries the issue counted in one <c>Scene3D.Render</c>.</summary>
        static int ReadEveryEyePath(FollowCamera3D cam)
        {
            _ = cam.Eye;
            _ = cam.Forward;
            _ = cam.View;
            _ = cam.ViewProjection;
            _ = cam.AbsoluteViewProjection;
            cam.WorldToScreen(Vector3.Zero, 800, 600, out _);
            _ = cam.ScreenToRay(new Vector2(400f, 300f), 800, 600);
            _ = cam.ScreenToGround(new Vector2(400f, 300f), 800, 600);
            return 8;
        }

        [Fact]
        public void Thirty_three_reads_in_one_frame_cost_exactly_one_sweep()
        {
            using var world = new CountingPhysicsWorld { WallDistance = 6.25f };
            FollowCamera3D cam = Camera(world);
            cam.BeginFrame();

            int reads = 0;
            for (int i = 0; i < 4; i++) reads += ReadEveryEyePath(cam);
            _ = cam.Eye;
            reads++;

            Assert.Equal(33, reads);                        // the issue's own count, reproduced
            Assert.Equal(1, world.SweepCount);              // counted by the world, not by the camera
            Assert.Equal(1L, cam.OcclusionSweepCount);      // and the camera agrees
            Assert.Equal(1L, cam.EyeComputeCount);
        }

        [Fact]
        public void A_setter_between_reads_costs_one_more_sweep()
        {
            using var world = new CountingPhysicsWorld { WallDistance = 6.25f };
            FollowCamera3D cam = Camera(world);
            cam.BeginFrame();

            Vector3 before = cam.Eye;
            Assert.Equal(1, world.SweepCount);

            _ = cam.Eye;
            Assert.Equal(1, world.SweepCount);              // no change, no recompute

            cam.Yaw = 1.2f;                                 // the caller changed the camera, mid-frame
            Vector3 after = cam.Eye;

            Assert.Equal(2, world.SweepCount);
            Assert.NotEqual(before, after);                 // and it answered with the POST-change eye
            _ = cam.Eye;
            Assert.Equal(2, world.SweepCount);              // then settles again on the new inputs
        }

        [Fact]
        public void Every_knob_that_feeds_the_eye_invalidates_it()
        {
            // One row per input, because the cache key is hand-maintained: a knob added to the geometry and
            // forgotten in the key is exactly the bug this catches, and it fails as a stale eye rather than as a
            // crash. The base camera has BOTH optional stages live (a wall to pull in against, ground to lift off)
            // so every knob has something to change.
            var mutations = new List<(string Name, Action<FollowCamera3D> Apply)>
            {
                ("Target", c => c.Target = new Vector3(3f, 0f, 0f)),
                ("Yaw", c => c.Yaw = 0.7f),
                ("Pitch", c => c.Pitch = 0.5f),
                ("Distance", c => c.Distance = 4f),
                ("HeightOffset", c => c.HeightOffset = 2f),
                ("OcclusionRadius", c => c.OcclusionRadius = 0.75f),
                ("OcclusionSkin", c => c.OcclusionSkin = 1.5f),
                ("MinOcclusionDistance", c => c.MinOcclusionDistance = 9f),
                ("GroundHeight", c => c.GroundHeight = (_, _) => 50f),
                ("GroundClearance", c => c.GroundClearance = 4f),
            };

            foreach ((string name, Action<FollowCamera3D> apply) in mutations)
            {
                using var world = new CountingPhysicsWorld { WallDistance = 6.25f };
                FollowCamera3D cam = Camera(world);
                cam.GroundHeight = (_, _) => 3f;
                cam.BeginFrame();

                Vector3 before = cam.Eye;
                apply(cam);
                Vector3 after = cam.Eye;

                Assert.True(before != after, $"{name} did not move the eye, so it proves nothing about the cache");
                Assert.Equal(2L, cam.EyeComputeCount);
            }
        }

        [Fact]
        public void Advancing_the_damped_target_invalidates_the_eye()
        {
            // The one input that moves without any field being assigned: EffectiveTarget eases toward Target on
            // each AdvanceTarget, so the cache key reads the EFFECTIVE target rather than the raw one.
            using var world = new CountingPhysicsWorld();
            FollowCamera3D cam = Camera(world);
            cam.EnableTargetDamping = true;
            cam.AdvanceTarget(1f / 60f);            // first call locks the smoothed target onto Target
            cam.Target = new Vector3(20f, 0f, 0f);  // a big jump, so one eased step is clearly visible

            cam.BeginFrame();
            Vector3 before = cam.Eye;
            cam.AdvanceTarget(1f / 60f);
            Vector3 after = cam.Eye;

            Assert.NotEqual(before, after);
            Assert.Equal(2L, cam.EyeComputeCount);
        }

        [Fact]
        public void A_new_frame_recomputes_after_the_world_moved_underneath_the_camera()
        {
            // The row that protects against the obvious over-caching bug. Nothing about the camera changes here:
            // the WORLD does, which no cache key on a camera field can ever see.
            using var world = new CountingPhysicsWorld();   // frame 1: nothing along the boom
            FollowCamera3D cam = Camera(world);

            cam.BeginFrame();
            Vector3 frame1 = cam.Eye;
            for (int i = 0; i < 4; i++) ReadEveryEyePath(cam);
            Assert.Equal(1, world.SweepCount);
            Assert.True(MathF.Abs(Vector3.Distance(frame1, cam.Target) - 10f) < 1e-3f, frame1.ToString());

            world.WallDistance = 6.25f;             // a wall slides in between the frames

            Assert.Equal(frame1, cam.Eye);          // still THIS frame, so the cached eye is still the answer
            Assert.Equal(1, world.SweepCount);

            cam.BeginFrame();                       // frame 2
            Vector3 frame2 = cam.Eye;

            Assert.Equal(2, world.SweepCount);
            Assert.NotEqual(frame1, frame2);
            // Pulled in to the wall minus probe radius minus skin, so the cache did not carry frame 1's eye across
            // the boundary.
            float boom = Vector3.Distance(frame2, cam.Target);
            Assert.True(MathF.Abs(boom - BoomAfterPullIn(cam, 6.25f)) < 1e-3f, boom.ToString());
        }

        [Fact]
        public void Occlusion_off_issues_no_sweeps_and_the_eye_stays_pure_arithmetic()
        {
            FollowCamera3D cam = Camera(null);
            cam.BeginFrame();

            for (int i = 0; i < 4; i++) ReadEveryEyePath(cam);

            Assert.Equal(0L, cam.OcclusionSweepCount);   // no world, so nothing to sweep
            Assert.Equal(1L, cam.EyeComputeCount);       // and the arithmetic path caches too
            // The same expectation the pre-cache geometry rows pin: yaw 0, pitch 0, no height offset puts the eye
            // one Distance along +Z from the target.
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0f, 0f, 10f)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void A_cached_eye_is_byte_identical_to_a_freshly_computed_one()
        {
            // The golden-safety row: caching is only invisible if the value handed back is the SAME value, not a
            // near one. An awkward framing (occlusion pull-in, then a ground lift on top of it) so both branches of
            // the geometry are inside the compared number.
            using var world = new CountingPhysicsWorld { WallDistance = 5.5f };
            FollowCamera3D cam = Camera(world);
            cam.Yaw = 0.9f;
            cam.Pitch = 0.4f;
            cam.HeightOffset = 1.25f;
            cam.GroundHeight = (x, z) => 2f + x * 0.1f - z * 0.05f;
            cam.GroundClearance = 0.75f;

            cam.BeginFrame();
            Vector3 cached = cam.Eye;
            _ = cam.Eye;
            cam.BeginFrame();                            // force the identical computation again
            Vector3 recomputed = cam.Eye;

            Assert.Equal(cached.X, recomputed.X);
            Assert.Equal(cached.Y, recomputed.Y);
            Assert.Equal(cached.Z, recomputed.Z);
            Assert.Equal(2L, cam.EyeComputeCount);
        }

        [Fact]
        public void InvalidateEye_is_the_manual_lever_for_a_consumer_without_a_scene()
        {
            using var world = new CountingPhysicsWorld();
            FollowCamera3D cam = Camera(world);

            _ = cam.Eye;
            world.WallDistance = 3f;
            _ = cam.Eye;
            Assert.Equal(1, world.SweepCount);

            cam.InvalidateEye();
            _ = cam.Eye;
            Assert.Equal(2, world.SweepCount);
        }

        /// <summary>
        /// The wiring row: <c>Scene3D.Begin</c> is what makes the cache one frame deep for every consumer that
        /// renders through a scene, so it is worth one live scene to prove the call is really there. It needs a
        /// device only because <c>Scene3D</c>'s constructor does, so it is gated like every other scene test.
        /// </summary>
        [GpuFact]
        public void Scene3D_Begin_latches_the_camera_frame()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);

            using var world = new CountingPhysicsWorld();
            FollowCamera3D cam = Camera(world);
            scene.CameraOverride = cam;

            scene.Begin();
            Vector3 frame1 = cam.Eye;
            world.WallDistance = 6.25f;             // the world moves between the scene's frames
            Assert.Equal(frame1, cam.Eye);          // and this frame keeps answering with what it computed

            scene.Begin();
            Vector3 frame2 = cam.Eye;

            Assert.NotEqual(frame1, frame2);
            float boom = Vector3.Distance(frame2, cam.Target);
            Assert.True(MathF.Abs(boom - BoomAfterPullIn(cam, 6.25f)) < 1e-3f, boom.ToString());
        }
    }
}
