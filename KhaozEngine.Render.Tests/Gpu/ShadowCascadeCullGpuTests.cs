using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The fidelity proof for the per-cascade shadow caster cull: every scene here renders TWICE, once with
    /// <see cref="Scene3D.ShadowCascadeCulling"/> on and once off, and the two frames must be byte-identical.
    /// <para>
    /// That is a stronger guard than a committed golden. A golden pins one image and moves when anything upstream
    /// of it moves. This pins the cull's own contribution to zero regardless of what else changes, which is exactly
    /// the claim being made (culling drops only geometry the rasterizer would have clipped anyway). Each test also
    /// asserts the cull was NOT a no-op on that scene, so a bug that quietly disabled it could not pass by making
    /// both halves the same work.
    /// </para>
    /// <para>
    /// The scenes are chosen for the ways the cull could go wrong: a caster past a near cascade that must fall
    /// through to a wider one, a grazing-sun caster far up-light of the near plane (the 17.13.0 pancaking contract,
    /// issue #394 - the one plane the cull must never test), and a dissolve-mixed span list whose per-span pipeline
    /// kinds must survive being split. Gated on KE_GPU_TESTS.
    /// </para>
    /// </summary>
    public sealed class ShadowCascadeCullGpuTests
    {
        const int W = 400, H = 280;

        readonly ITestOutputHelper _out;
        public ShadowCascadeCullGpuTests(ITestOutputHelper o) => _out = o;

        static long Diff(byte[] a, byte[] b)
        {
            long d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d;
        }

        // Render one scene with the cull on, with it off, and with shadows off entirely. The first two must match
        // byte for byte. The third is the reference that proves the scene renders a VISIBLE shadow at all, so the
        // identity assert is never vacuous (an all-lit frame would trivially match itself). Two frames each,
        // matching the golden suite (frame 1 fits, frame 2 is the settled one).
        Result RenderBoth(Action<Scene3D> setup, Action<Scene3D> drawFrame)
        {
            byte[] Run(bool culling, bool shadows, out int[] counts, out int candidates)
            {
                using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
                using var preview = new Render3DPreview(ctx.GpuDevice, W, H);
                preview.Scene.ShadowCascadeCulling = culling;
                setup(preview.Scene);
                if (!shadows) preview.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
                preview.Capture(drawFrame);
                preview.Capture(drawFrame);
                counts = new int[4];
                for (int c = 0; c < 4; c++) counts[c] = preview.Scene.ShadowCascadeCasterCount(c);
                candidates = preview.Scene.ShadowCasterCandidateCount;
                return preview.ReadbackRgba();
            }

            byte[] culled = Run(true, true, out int[] culledCounts, out int candidates);
            byte[] full = Run(false, true, out _, out _);
            byte[] unshadowed = Run(true, false, out _, out _);
            return new Result(culled, full, unshadowed, culledCounts, candidates);
        }

        sealed record Result(byte[] Culled, byte[] Full, byte[] Unshadowed, int[] CulledCounts, int Candidates);

        // The scene really casts a shadow (so pixel identity below means something), and the cull really dropped
        // something on it (so identity is not just "both halves did the same work").
        void AssertIdentical(string scene, Result r, long minShadowSignal = 20000)
        {
            long signal = Diff(r.Culled, r.Unshadowed);
            _out.WriteLine($"{scene}: candidates {r.Candidates}, drawn per cascade [{string.Join(", ", r.CulledCounts)}], shadow signal {signal}");
            Assert.True(signal > minShadowSignal,
                $"{scene} renders no visible shadow (signal {signal} against the shadows-off frame), so pixel identity proves nothing");
            Assert.True(r.CulledCounts[0] < r.Candidates,
                $"{scene}: cascade 0 drew every candidate ({r.CulledCounts[0]} of {r.Candidates}), so the cull did nothing here");
            Assert.Equal(0, Diff(r.Culled, r.Full));
        }

        // A field of casters spread far past the near cascade, so most of them are outside cascade 0 and 1 and only
        // the wide outer cascades cover them. If XY culling were wrong in either direction (too eager, or applied to
        // the wrong cascade) a shadow would move or vanish.
        [GpuFact]
        public void Wide_scene_renders_identically_with_and_without_culling()
        {
            MeshHandle floor = default, box = default;
            var r = RenderBoth(
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(300f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Azimuth = 0.5f;
                    scene.Camera.Elevation = 0.7f;
                    scene.Camera.Frame(new Vector3(0f, 0f, 22f), new Vector3(20f, 6f, 52f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 22f), new Color(0.60f, 0.61f, 0.63f, 1f));
                    // A row of casters from just in front of the camera out to well past the near cascades.
                    for (int i = 0; i < 24; i++)
                    {
                        float z = 2f + i * 4f;
                        scene.Draw(box, Matrix4x4.CreateScale(1f, 1.8f, 1f) * Matrix4x4.CreateTranslation(((i % 3) - 1) * 6f, 1.2f, z),
                            new Color(0.2f, 0.75f, 0.25f, 1f));
                    }
                });

            AssertIdentical("wide", r);
        }

        // The grazing-sun case the near plane must never be culled against (issue #394): a tall caster standing well
        // up-sun of the camera, far in front of the near cascade's near plane, whose shadow stripe runs across the
        // visible ground. A near-plane cull would delete it, and the ground would render lit in the culled half.
        [GpuFact]
        public void Low_sun_up_light_caster_renders_identically_with_and_without_culling()
        {
            const float sunElevation = 15f, casterZ = -32f, casterHeight = 16f, casterWidth = 2f;
            float e = sunElevation * MathF.PI / 180f;
            var sun = Vector3.Normalize(new Vector3(0f, -MathF.Sin(e), MathF.Cos(e)));
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 5f, -8f),
                Yaw = 0f,
                Pitch = -0.30f,
                FieldOfView = 0.9f,
                AspectRatio = (float)W / H,
                NearPlane = 0.5f,
                FarPlane = 160f,
            };

            MeshHandle floor = default, caster = default;
            var r = RenderBoth(
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(160f, 0.1f));
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.CameraOverride = fly;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = sun;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    scene.Draw(caster,
                        Matrix4x4.CreateScale(casterWidth, casterHeight, casterWidth)
                        * Matrix4x4.CreateTranslation(0f, casterHeight * 0.5f, casterZ),
                        new Color(0.2f, 0.75f, 0.25f, 1f));
                    // Decoys far off to the side, so the cull has something it legitimately CAN drop and the scene
                    // still proves the up-light caster itself survives.
                    for (int i = 0; i < 12; i++)
                        scene.Draw(caster, Matrix4x4.CreateScale(2f, 4f, 2f) * Matrix4x4.CreateTranslation(-70f + i * 3f, 2f, 60f),
                            new Color(0.7f, 0.3f, 0.2f, 1f));
                });

            AssertIdentical("low-sun", r);
        }

        // A caster placed OUTSIDE cascade 0's light-space footprint but comfortably inside a wider cascade: the
        // fall-through the whole design depends on. Cascade 0 must reject it, an outer cascade must keep it, and the
        // rendered shadow must be identical to drawing it into every cascade. Two distinct caster meshes, so each
        // one is its own span and the merge gap cannot blur the counts. The ground receives but does not cast, so
        // the caster counts are exactly the two boxes.
        [GpuFact]
        public void A_caster_outside_cascade_zero_still_shadows_via_the_next_cascade()
        {
            MeshHandle floor = default, nearBox = default, farBox = default;
            var r = RenderBoth(
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(300f, 0.1f));
                    nearBox = scene.LoadMesh(MeshPrimitives.Box(1.5f));
                    farBox = scene.LoadMesh(MeshPrimitives.Box(1.6f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.Quality.Shadows.ShadowNearDistance = 8f;   // a tight cascade 0
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Azimuth = 0.5f;
                    scene.Camera.Elevation = 0.6f;
                    scene.Camera.Frame(new Vector3(0f, 0f, 20f), new Vector3(18f, 6f, 50f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 20f), new Color(0.60f, 0.61f, 0.63f, 1f),
                        Material.None, false);
                    scene.Draw(nearBox, Matrix4x4.CreateScale(1f, 2.0f, 1f) * Matrix4x4.CreateTranslation(0f, 1.6f, 4f),
                        new Color(0.2f, 0.75f, 0.25f, 1f));
                    scene.Draw(farBox, Matrix4x4.CreateScale(1f, 2.2f, 1f) * Matrix4x4.CreateTranslation(0f, 1.8f, 44f),
                        new Color(0.85f, 0.35f, 0.15f, 1f));
                });

            Assert.Equal(2, r.Candidates);
            // Cascade 0 is fitted to an 8 m near slice, so it reaches the near box and not the one 44 m out.
            Assert.Equal(1, r.CulledCounts[0]);
            Assert.Contains(r.CulledCounts, c => c == 2);   // some wider cascade covers both, which is what keeps the far shadow
            AssertIdentical("fall-through", r);
        }

        // A mixed span list (plain casters, dissolving casters, and an inverted-dissolve HLOD half) must survive the
        // split with each sub-span keeping its own depth pipeline. Splitting a run in the wrong place would either
        // bind the wrong pipeline or drop a kind, and both show up as a pixel difference.
        [GpuFact]
        public void Mixed_dissolve_kinds_render_identically_with_and_without_culling()
        {
            MeshHandle floor = default, box = default;
            var r = RenderBoth(
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(240f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.5f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Azimuth = 0.5f;
                    scene.Camera.Elevation = 0.65f;
                    scene.Camera.Frame(new Vector3(0f, 0f, 16f), new Vector3(16f, 6f, 44f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 16f), new Color(0.60f, 0.61f, 0.63f, 1f));
                    var tint = new Color(0.3f, 0.65f, 0.9f, 1f);
                    var edge = new Color(1f, 0.6f, 0.2f, 1f);
                    for (int i = 0; i < 30; i++)
                    {
                        Matrix4x4 w = Matrix4x4.CreateScale(1f, 1.6f, 1f)
                            * Matrix4x4.CreateTranslation(((i % 5) - 2) * 7f, 1.2f, 2f + (i / 5) * 9f);
                        switch (i % 3)
                        {
                            case 0: scene.Draw(box, w, tint); break;
                            case 1: scene.Draw(box, w, tint, Material.None, 0.45f, 0.08f, edge, true); break;
                            default: scene.Draw(box, w, tint, Material.None, 0.55f, 0.08f, edge, true, true); break;
                        }
                    }
                });

            AssertIdentical("mixed-dissolve", r);
        }

        // The atlas-reuse contract is unchanged by culling: a static scene still skips the depth pass on the second
        // frame, and a moved caster still re-renders. The signature is deliberately still the FULL caster list.
        [GpuFact]
        public void Culling_does_not_change_the_dirty_skip_behaviour()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);
            Scene3D scene = preview.Scene;
            scene.Post.Starfield = false;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(40f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.2f));

            void Draw(float x, Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(x, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
                // A far caster cascade 0 rejects, so the cull is live while the skip is being measured.
                s.Draw(box, Matrix4x4.CreateTranslation(80f, 0.7f, 60f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }

            preview.Capture(s => Draw(-1.4f, s));
            Assert.False(scene.ShadowPassSkippedLastFrame, "the first shadow frame must render");
            preview.Capture(s => Draw(-1.4f, s));
            Assert.True(scene.ShadowPassSkippedLastFrame, "an unchanged static scene must still skip with culling on");
            preview.Capture(s => Draw(1.4f, s));
            Assert.False(scene.ShadowPassSkippedLastFrame, "a moved caster must still re-render with culling on");
        }
    }
}
