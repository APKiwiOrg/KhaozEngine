using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Camera-relative rendering, the visual half: the same scene rendered at the world origin and at 100 km must
    /// look the same. This is the test that proves the feature, because the failure mode it guards is vertex swim
    /// and jitter that no numeric assertion describes.
    /// <para>
    /// It carries "Golden" in the name so the cross-platform GPU matrix runs it on every backend, but it commits NO
    /// reference grid: it is the property flavour of golden (see <see cref="GoldenCompare"/>'s naming contract),
    /// comparing two grids IT rendered against each other under the same per-channel tolerance. That is deliberate
    /// and it is the stronger form here. A committed grid would need a three-backend bake to land, would drift with
    /// every unrelated lighting change, and would only ever assert that this scene still looks like itself; the
    /// self-comparison asserts the thing the release actually claims, which is that distance from the origin stops
    /// mattering.
    /// </para>
    /// </summary>
    public sealed class FarFromOriginGoldenTests
    {
        const int W = 480, H = 320;
        // 100 km on both planar axes, the offset the design is sized against. Y is never framed, so the scene keeps
        // its own heights.
        static readonly Vector3 Far = new(100_000f, 0f, 100_000f);

        /// <summary>
        /// The scene, built around <paramref name="at"/> so the identical geometry can be placed at the origin or
        /// 100 km out. Deliberately broad: every payload that carries a world position through a DIFFERENT reduction
        /// site is represented, so a site that forgot the subtraction moves this test rather than shipping.
        /// Model matrices (the upload staging copy), a point light (the frame UBO light span), a ground decal (the
        /// decal pass), an alpha billboard and a particle (their post-sort expansions), a debug line (the immediate
        /// vertex queue), and cascaded shadows (the render-relative cascade matrices).
        /// </summary>
        static byte[] Render(Vector3 at, Action<Scene3D>? configure = null)
        {
            MeshHandle floor = default, tall = default, wide = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    tall = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    wide = scene.LoadMesh(MeshPrimitives.Box(2.4f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Azimuth = 0.6f;
                    scene.Camera.Elevation = 0.65f;
                    scene.Camera.FarPlane = 400f;
                    scene.Camera.Frame(at, new Vector3(26f, 8f, 26f));
                    configure?.Invoke(scene);
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(at), new Color(0.55f, 0.57f, 0.60f, 1f));
                    scene.Draw(tall, Matrix4x4.CreateScale(1f, 2.2f, 1f) * Matrix4x4.CreateTranslation(at + new Vector3(-4f, 1.3f, 2f)),
                        new Color(0.20f, 0.72f, 0.30f, 1f));
                    scene.Draw(wide, Matrix4x4.CreateTranslation(at + new Vector3(5f, 1.2f, -3f)),
                        new Color(0.86f, 0.34f, 0.16f, 1f));
                    scene.AddLight(at + new Vector3(0f, 3f, 6f), new Color(1f, 0.7f, 0.35f, 1f), 14f, 2.5f);
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Circle,
                        Center = at + new Vector3(0f, 0f, 0f),
                        Size = new Vector4(3.5f, 0f, 0f, 0f),
                        FillColor = new Color(0.15f, 0.55f, 0.95f, 0.7f),
                        OutlineColor = new Color(0.6f, 0.9f, 1f, 0.9f),
                        EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha,
                        YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                    scene.DrawBillboard(at + new Vector3(-2f, 3.5f, -5f), 1.1f, new Color(0.95f, 0.85f, 0.25f, 0.75f));
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = at + new Vector3(3f, 3.0f, 4f),
                        Size = 0.9f,
                        Color = new Color(0.9f, 0.35f, 0.85f, 0.7f),
                        Shape = ParticleShape.SoftGlow,
                    });
                    scene.DebugLine(at + new Vector3(-8f, 0.2f, -8f), at + new Vector3(8f, 4.2f, 8f),
                        new Color(1f, 1f, 1f, 1f));
                },
                frames: 2);
        }

        [GpuFact]
        public void Golden3D_FarFromOrigin_MatchesTheSameSceneAtTheOrigin()
        {
            // Test 19. Both renders take the camera-relative path by default (the far one latches a non-zero origin,
            // the origin one latches zero), and the images must agree within the same per-channel tolerance the
            // committed goldens are held to. Before this release the far image was visibly wrong: the matrix
            // concatenation ran on two ~1e5 operands, so geometry swam and shadow edges crawled.
            // What this one really guards is COMPLETENESS of the reduction sites rather than precision: a payload
            // that missed its subtraction is displaced by the whole render origin, so it leaves the frustum and the
            // image loses it entirely. The precision half is the close-up below.
            float worst = WorstCellDelta(GoldenCompare.Downsample(Render(Vector3.Zero), W, H),
                                         GoldenCompare.Downsample(Render(Far), W, H));
            Assert.True(worst <= GoldenCompare.Tolerance,
                $"the scene at {Far} differs from the same scene at the origin by {worst} " +
                $"(tolerance {GoldenCompare.Tolerance}): camera-relative rendering is not holding at range.");
        }

        /// <summary>
        /// A tight close-up: a 12 cm box filling the frame, so ONE float32 quantum at 100 km (7.8 mm, the ULP of the
        /// binade the coordinate sits in) is several pixels wide instead of a tenth of one. The broad scene above is
        /// a completeness check on the reduction sites; this is the precision check, and it is the one that can tell
        /// the two paths apart at all.
        /// </summary>
        static byte[] RenderCloseUp(Vector3 at, Action<Scene3D>? configure = null)
        {
            MeshHandle box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(0.12f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Azimuth = 0.37f;          // off-axis, so the box edges cut cells diagonally
                    scene.Camera.Elevation = 0.44f;
                    scene.Camera.Target = at;
                    scene.Camera.OrthoSize = 0.22f;        // ~0.7 mm per pixel: one 7.8 mm quantum is ~11 px
                    scene.Camera.Distance = 4f;
                    scene.Camera.NearPlane = 0.05f;
                    scene.Camera.FarPlane = 20f;
                    scene.Camera.AspectRatio = (float)W / H;
                    configure?.Invoke(scene);
                },
                drawFrame: scene => scene.Draw(box, Matrix4x4.CreateTranslation(at), new Color(0.85f, 0.80f, 0.30f, 1f)),
                frames: 2);
        }

        [GpuFact]
        public void Golden3D_FarFromOrigin_CloseUpIsPreciseAndTheAbsolutePathIsNot()
        {
            // Both halves, because the first half alone would pass on a harness that cannot see the failure it
            // claims to prevent (the broad scene above is exactly that: at its zoom, a 7.8 mm quantum is a tenth of
            // a pixel and BOTH paths pass). Half one: camera-relative rendering at 100 km reproduces the origin
            // image within the committed goldens' own tolerance. Half two, the known-failing baseline: the SAME
            // scene at 100 km with the origin opted out of does not, which is what makes half one mean something.
            float[] origin = GoldenCompare.Downsample(RenderCloseUp(Vector3.Zero), W, H);
            float[] relative = GoldenCompare.Downsample(RenderCloseUp(Far), W, H);
            float[] absolute = GoldenCompare.Downsample(RenderCloseUp(Far, s => s.RenderOrigin = Vector3.Zero), W, H);

            // The box has to actually be in frame, or "both blank" would satisfy half one for free.
            Assert.True(WorstCellDelta(origin, new float[origin.Length]) > 0.3f,
                "the close-up rendered nothing bright enough to be the box: the comparison below is vacuous");

            float relativeWorst = WorstCellDelta(origin, relative);
            float absoluteWorst = WorstCellDelta(origin, absolute);
            Assert.True(relativeWorst <= GoldenCompare.Tolerance,
                $"the camera-relative close-up at {Far} differs from the origin one by {relativeWorst} " +
                $"(tolerance {GoldenCompare.Tolerance})");
            Assert.True(absoluteWorst > GoldenCompare.Tolerance,
                $"the ABSOLUTE close-up at {Far} differs from the origin one by only {absoluteWorst}, inside the " +
                $"{GoldenCompare.Tolerance} tolerance: this scene no longer distinguishes the two paths, so the " +
                "assertion above proves nothing. Zoom in further or move further out.");
        }

        [GpuFact]
        public void Golden3D_FarFromOrigin_ZeroOptOutAndConsumerCameraFallbackAreTheSameAbsolutePath()
        {
            // Tests 18 and 19b together, and together is the point. Both of these must render through the WHOLE
            // pre-release absolute pipeline:
            //   - RenderOrigin = Vector3.Zero, the explicit opt-out for a consumer with goldens it has not rebaked;
            //   - a consumer camera that implements IIsoCamera3D but NOT IRenderOriginAware, which falls the whole
            //     pipeline back rather than half-applying an origin the camera cannot honour.
            // Asserting they are BYTE-identical is what makes both claims checkable at once: if any reduction leaked
            // into either path, or the fallback were partial, the two images would differ. Neither reports the origin
            // as active.
            byte[] optOut = Render(Far, scene =>
            {
                scene.RenderOrigin = Vector3.Zero;
                Assert.False(scene.RenderOriginActive);   // reads back before the first Begin latches
            });
            byte[] fallback = Render(Far, scene => scene.CameraOverride = new PlainConsumerCamera(scene.Camera));

            Assert.Equal(optOut.Length, fallback.Length);
            for (int i = 0; i < optOut.Length; i++)
                if (optOut[i] != fallback[i])
                    Assert.Fail($"byte {i} differs ({optOut[i]} vs {fallback[i]}): the RenderOrigin = Zero opt-out and " +
                        "the non-origin-aware consumer camera are not the same absolute path.");
        }

        [GpuFact]
        public void The_render_origin_latches_at_begin_and_ignores_a_write_mid_frame()
        {
            // A frame that submitted half its geometry against one origin and uploaded it against another would be
            // displaced by the difference, and stable enough between re-anchors to read as a content bug. So the
            // value in force is read once, at Begin.
            Vector3 latched = default, duringFrame = default;
            int frame = 0;
            Render3DSnapshot.Capture(W, H,
                setup: scene => scene.Camera.Frame(Far, new Vector3(20f, 6f, 20f)),
                drawFrame: scene =>
                {
                    if (frame++ == 0)
                    {
                        latched = scene.RenderOrigin;
                        scene.RenderOrigin = new Vector3(1_234f, 5f, 6_789f);   // ignored until the next Begin
                        duringFrame = scene.RenderOrigin;
                    }
                },
                frames: 1);

            Assert.Equal(WorldFrame.Nearest(latched).Anchor, latched);   // quantized, so it cannot jitter per frame
            Assert.NotEqual(Vector3.Zero, latched);                      // the camera really is far from the origin
            Assert.Equal(latched, duringFrame);
        }

        /// <summary>The largest per-channel difference between two downsampled grids, the same metric
        /// <see cref="GoldenCompare"/> holds the committed goldens to.</summary>
        static float WorstCellDelta(float[] a, float[] b)
        {
            float worst = 0f;
            for (int i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
            return worst;
        }

        /// <summary>
        /// A consumer's own camera: the read-only <see cref="IIsoCamera3D"/> surface and nothing else, which is
        /// exactly what release 1 promises keeps working unchanged. Delegates to a real camera so the pose is a
        /// sensible one; the point is only that it does not implement <see cref="IRenderOriginAware"/>.
        /// </summary>
        sealed class PlainConsumerCamera : IIsoCamera3D
        {
            readonly IsoCamera3D _inner;
            public PlainConsumerCamera(IsoCamera3D inner) => _inner = inner;
            public Matrix4x4 View => _inner.View;
            public Matrix4x4 Projection => _inner.Projection;
            public Matrix4x4 ViewProjection => _inner.ViewProjection;
            public Vector3 Eye => _inner.Eye;
            public Vector3 Forward => _inner.Forward;
            public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel) =>
                _inner.WorldToScreen(world, viewportWidth, viewportHeight, out screenPixel);
        }
    }
}
