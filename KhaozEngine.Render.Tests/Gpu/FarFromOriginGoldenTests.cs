using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
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
    /// every unrelated lighting change, and would only ever assert that this scene still looks like itself. The
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
        static byte[] Render(Vector3 at, Action<Scene3D>? configure = null, Action<Scene3D>? onDrawFrame = null)
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
                    onDrawFrame?.Invoke(scene);
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
            float[] origin = GoldenCompare.Downsample(Render(Vector3.Zero), W, H);
            float[] far = GoldenCompare.Downsample(Render(Far), W, H);

            // The scene has to actually be in frame, or "both blank" would satisfy the comparison below for free
            // (same anti-vacuity guard as the close-up test).
            Assert.True(WorstCellDelta(origin, new float[origin.Length]) > 0.3f,
                "the broad scene rendered nothing bright enough to be meaningful: the comparison below is vacuous");

            float worst = WorstCellDelta(origin, far);
            Assert.True(worst <= GoldenCompare.Tolerance,
                $"the scene at {Far} differs from the same scene at the origin by {worst} " +
                $"(tolerance {GoldenCompare.Tolerance}): camera-relative rendering is not holding at range.");
        }

        /// <summary>
        /// A tight close-up: a 12 cm box filling the frame, so ONE float32 quantum at 100 km (7.8 mm, the ULP of the
        /// binade the coordinate sits in) is several pixels wide instead of a tenth of one. The broad scene above is
        /// a completeness check on the reduction sites. This is the precision check, and it is the one that can tell
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
            //   - RenderOrigin = Vector3.Zero, the explicit opt-out for a consumer with goldens it has not rebaked
            //   - a consumer camera that implements IIsoCamera3D but NOT IRenderOriginAware, which falls the whole
            //     pipeline back rather than half-applying an origin the camera cannot honour
            // Asserting they are BYTE-identical is what makes both claims checkable at once: if any reduction leaked
            // into either path, or the fallback were partial, the two images would differ. Neither reports the origin
            // as active.
            byte[] optOut = Render(Far,
                configure: scene => scene.RenderOrigin = Vector3.Zero,
                onDrawFrame: scene => Assert.False(scene.RenderOriginActive));   // reads back AFTER Begin latches
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
                        latched = scene.RenderOrigin!.Value;
                        scene.RenderOrigin = new Vector3(1_234f, 5f, 6_789f);   // ignored until the next Begin
                        duringFrame = scene.RenderOrigin!.Value;
                    }
                },
                frames: 1);

            Assert.Equal(WorldFrame.Nearest(latched).Anchor, latched);   // quantized, so it cannot jitter per frame
            Assert.NotEqual(Vector3.Zero, latched);                      // the camera really is far from the origin
            Assert.Equal(latched, duringFrame);
        }

        /// <summary>
        /// The terrain half, which camera-relative rendering alone could not fix: the vertices were baked absolute,
        /// so at 100 km the grid positions were already quantized to that magnitude's 7.8 mm float32 lattice before
        /// anything rendered them. Chunk-local vertices plus a per-chunk placement matrix put the same geometry on
        /// the same lattice wherever the chunk sits, and this renders both and compares.
        /// <para>Deliberately close-up and grazing, for the same reason as the box close-up above: at a broad
        /// top-down zoom a 7.8 mm vertex displacement is a fraction of a pixel and both paths would pass.</para>
        /// </summary>
        // A height field whose shape depends only on the offset from a reference point, so the SAME terrain can be
        // meshed at the origin and at 100 km and the two are comparable at all. A world-space preset is not
        // translation-invariant (its noise is keyed on the absolute coordinate), so it would produce two different
        // hills and the comparison would mean nothing.
        sealed class LocalBumps : ITerrainFeature
        {
            readonly float _refX, _refZ;
            public LocalBumps(float refX, float refZ) { _refX = refX; _refZ = refZ; }
            public float Apply(float x, float z, float h) =>
                1.5f * MathF.Sin((x - _refX) * 0.35f) * MathF.Cos((z - _refZ) * 0.27f);
        }

        static byte[] RenderTerrain(Vector3 at)
        {
            var field = new TerrainField(new TerrainConfig
            {
                GentleAmplitude = 0f,
                Biomes = new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, BaseHeight = 0f, HillAmplitude = 0f } },
                Features = new ITerrainFeature[] { new LocalBumps(at.X, at.Z) },
            });
            var region = new TerrainChunkRegion { OriginX = at.X, OriginZ = at.Z, Size = 32f };
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 0);

            MeshHandle h = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // The untextured (vertex-colour ramp) path deliberately: the splat shader's triplanar UV is
                    // anchored to the ABSOLUTE world position, which this release explicitly does not fix, so a
                    // textured comparison would measure that known residual instead of the geometry.
                    h = scene.LoadTerrainChunk(chunk);
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Azimuth = 0.37f;
                    scene.Camera.Elevation = 0.30f;                       // grazing, so vertex displacement shows
                    scene.Camera.Target = at + new Vector3(16f, 0f, 16f);
                    scene.Camera.OrthoSize = 3.0f;
                    scene.Camera.Distance = 40f;
                    scene.Camera.NearPlane = 0.05f;
                    scene.Camera.FarPlane = 200f;
                    scene.Camera.AspectRatio = (float)W / H;
                },
                drawFrame: scene => scene.DrawTerrainChunk(h, region),
                frames: 2);
        }

        [GpuFact]
        public void Golden3D_FarFromOrigin_TerrainGeometryIsPreciseAtRange()
        {
            // The chunk-local bake, rendered end to end: the same chunk shape meshed at the origin and at 100 km
            // draws the same image, through the placement matrix rather than through baked-in vertex coordinates.
            // It commits no reference grid (it compares two grids it rendered itself, like its siblings above), so
            // it needs no cross-backend bake.
            //
            // What it binds is the PLACEMENT PATH: chunk-local vertices reaching the screen at the right place, at
            // range, with the cull's pure-translation fast path in force. It is not a discriminator against the old
            // absolute bake, and saying so is worth more than implying otherwise: at this zoom one 7.8 mm vertex
            // quantum is a fraction of a pixel, and the old bake rendered at range too (release 1's reduction rides
            // the model matrix, so an identity-drawn absolute chunk was reduced by the origin just the same). The
            // assertion that DOES pin the bake numerically is headless and bit-exact - see
            // TerrainChunkBuilderTests.Vertices_are_chunk_local_however_far_out_the_chunk_sits, where the far
            // chunk's planar lattice is compared against the origin chunk's exactly rather than through a camera.
            float[] origin = GoldenCompare.Downsample(RenderTerrain(Vector3.Zero), W, H);
            float[] far = GoldenCompare.Downsample(RenderTerrain(Far), W, H);

            Assert.True(WorstCellDelta(origin, new float[origin.Length]) > 0.3f,
                "the terrain rendered nothing bright enough to be meaningful: the comparison below is vacuous");

            float worst = WorstCellDelta(origin, far);
            Assert.True(worst <= GoldenCompare.Tolerance,
                $"the terrain chunk at {Far} differs from the same chunk at the origin by {worst} " +
                $"(tolerance {GoldenCompare.Tolerance}): the chunk-local bake is not holding at range.");
        }

        [GpuFact]
        public void Mid_frame_camera_swap_to_a_non_aware_camera_still_projects_absolute_geometry_correctly()
        {
            // M2 (review r1): FrameViewProjection's non-aware-camera fallback composed T(-origin) onto the
            // camera's own (never-shifted) view-projection. Geometry is ALREADY reduced by the origin at
            // submission (ToRender), so that second subtraction turned p_abs - O into p_abs - 2O: at 100 km this
            // pushes the geometry twice as far out and it leaves the frustum entirely. The fix composes T(+origin)
            // instead, adding the origin back so the fallback sees absolute geometry again, exactly what its own
            // view-projection expects. Swapping CameraOverride to a non-aware camera strictly BETWEEN Begin (which
            // latches the origin while scene.Camera, still aware, is active) and the render call is what reaches
            // this branch, and it must project the same point to (within the committed goldens' own tolerance)
            // the same pixel as the same camera set from the very start (the pure absolute path, never touched by
            // the origin machinery at all). Tolerance, not byte-exact: the fixed fallback composes an extra
            // T(+origin) * VP matrix multiply the pure absolute path never does, so a few ULPs of edge/AA noise
            // between the two paths is expected even when the fix is correct. A real double-subtraction bug pushes
            // the box a whole render origin away, well outside any float32-noise tolerance, so this still catches it.
            MeshHandle box = default;
            Vector3 boxSize = new(1.2f, 1.2f, 1.2f);

            byte[] Capture(bool swapMidFrame)
            {
                IsoCamera3D fresh = new() { AspectRatio = (float)W / H, FarPlane = 400f };
                fresh.Frame(Far, boxSize);   // never touched by Scene3D's origin machinery: a genuinely absolute VP
                return Render3DSnapshot.Capture(W, H,
                    setup: scene =>
                    {
                        box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                        scene.Post.Starfield = false;
                        scene.Post.Outline = false;
                        scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
                        scene.Camera.Frame(Far, boxSize);   // gives Begin something aware and nonzero to latch
                        if (!swapMidFrame) scene.CameraOverride = new PlainConsumerCamera(fresh);
                    },
                    drawFrame: scene =>
                    {
                        if (swapMidFrame) scene.CameraOverride = new PlainConsumerCamera(fresh);
                        scene.Draw(box, Matrix4x4.CreateTranslation(Far), new Color(0.85f, 0.80f, 0.30f, 1f));
                    },
                    frames: 1);
            }

            float[] swapped = GoldenCompare.Downsample(Capture(swapMidFrame: true), W, H);
            float[] absolute = GoldenCompare.Downsample(Capture(swapMidFrame: false), W, H);

            // The box has to actually be in frame, or "both blank" would satisfy the comparison below for free.
            Assert.True(WorstCellDelta(absolute, new float[absolute.Length]) > 0.3f,
                "the reference render was nothing bright enough to be the box: the comparison below is vacuous");

            float worst = WorstCellDelta(swapped, absolute);
            Assert.True(worst <= GoldenCompare.Tolerance,
                $"the mid-frame camera swap differs from the same camera active from Begin by {worst} " +
                $"(tolerance {GoldenCompare.Tolerance}): the non-aware fallback is not projecting absolute " +
                "geometry correctly.");
        }

        [GpuFact]
        public void Assigning_a_null_render_origin_restores_the_automatic_quantized_eye_default()
        {
            // m5 (review r1): the old Vector3-typed property could latch an explicit override but never clear it,
            // so a consumer that set RenderOrigin once could never get back to the automatic
            // WorldFrame.Nearest(Eye) default. RenderOrigin is Vector3? now: assigning null restores the automatic
            // default, exactly like never having set it. Three frames: frame 0 sets an explicit override (latches
            // for frame 1), frame 1 reads it back and clears it with null (latches for frame 2), frame 2 reads the
            // automatic default again.
            Vector3 explicitOverride = new(1_234f, 0f, 6_789f);
            Vector3 expectedAutomatic = default;
            Vector3? readAtFrame1 = null, readAtFrame2 = null;
            int frame = 0;
            Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Camera.Frame(Far, new Vector3(20f, 6f, 20f));
                    expectedAutomatic = WorldFrame.Nearest(scene.Camera.Eye).Anchor;
                },
                drawFrame: scene =>
                {
                    switch (frame++)
                    {
                        case 0:
                            scene.RenderOrigin = explicitOverride;
                            break;
                        case 1:
                            readAtFrame1 = scene.RenderOrigin;
                            scene.RenderOrigin = null;
                            break;
                        case 2:
                            readAtFrame2 = scene.RenderOrigin;
                            break;
                    }
                },
                frames: 3);

            Assert.Equal(explicitOverride, readAtFrame1);
            Assert.Equal(expectedAutomatic, readAtFrame2);
        }

        [GpuFact]
        public void Overriding_to_a_wrapper_around_the_still_aware_camera_zeroes_its_stale_origin()
        {
            // m6 (review r1): ApplyOriginToCamera only ever touched ActiveCamera, so switching from an aware
            // scene.Camera (driven to a nonzero origin O in frame 0) to a non-aware CameraOverride that wraps and
            // delegates to that SAME aware camera left its RenderOrigin stuck at O for frame 1: Scene3D itself
            // correctly treats frame 1 as absolute (RenderOriginActive false, geometry submitted unreduced), but
            // the wrapper's ViewProjection still came from a camera built against Eye - O, so the whole frame
            // displaced by O. LatchRenderOrigin now tracks which camera it last pushed the origin onto and zeroes
            // it the moment that camera stops being active, so frame 1 here must render byte-identical to a
            // reference where the wrapper was active the whole time and the origin was never latched nonzero at
            // all.
            MeshHandle box = default;
            Vector3 boxSize = new(1.2f, 1.2f, 1.2f);
            byte[] wrapped = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
                    scene.Camera.Frame(Far, boxSize);
                },
                drawFrame: scene =>
                {
                    bool firstFrame = scene.CameraOverride == null;
                    if (firstFrame)
                        Assert.True(scene.RenderOriginActive, "frame 0 should latch a nonzero origin on the aware camera");
                    scene.Draw(box, Matrix4x4.CreateTranslation(Far), new Color(0.85f, 0.80f, 0.30f, 1f));
                    if (firstFrame) scene.CameraOverride = new PlainConsumerCamera(scene.Camera);
                },
                frames: 2);

            byte[] reference = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
                    scene.Camera.Frame(Far, boxSize);
                    scene.CameraOverride = new PlainConsumerCamera(scene.Camera);
                },
                drawFrame: scene => scene.Draw(box, Matrix4x4.CreateTranslation(Far), new Color(0.85f, 0.80f, 0.30f, 1f)),
                frames: 1);

            Assert.Equal(wrapped.Length, reference.Length);
            for (int i = 0; i < wrapped.Length; i++)
                if (wrapped[i] != reference[i])
                    Assert.Fail($"byte {i} differs ({wrapped[i]} vs {reference[i]}): the wrapper camera is still " +
                        "reading a stale origin from the aware camera it delegates to.");
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
        /// sensible one. The point is only that it does not implement <see cref="IRenderOriginAware"/>.
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
