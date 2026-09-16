using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The pixel proof for shadow-only casters (issue #974): a slab queued through
    /// <see cref="Scene3D.DrawShadowOnly"/> shades the ground under it and is itself nowhere on screen.
    /// <para>
    /// Relative assertions only, against a BASELINE capture of the same scene with the slab left out, so nothing
    /// has to be baked on the three CI legs. The ground under the key light's shadow is darker than it is in the
    /// baseline, the pixel the slab would have occupied is byte-for-byte the baseline's, and the frame's own
    /// counters say the slab reached the shadow pass and not the colour pass. Two captures, which is under the
    /// class-fixture threshold, so this builds its own scene each time. Skipped unless KE_GPU_TESTS is set.
    /// </para>
    /// </summary>
    public sealed class ShadowOnlyCasterGpuTests
    {
        const int W = 400, H = 300;

        // A wide flat ground (top face at y = 0.1) in mid grey, so the key light's shadow reads clearly on it and
        // a slab that wrongly DREW would read clearly against it: DrawShadowOnly carries no tint, so a regression
        // that leaked it into the colour pass would paint white where this paints grey.
        static readonly Color GroundGrey = new(0.55f, 0.57f, 0.60f, 1f);

        // The caster: a 3 by 3 slab floating 3 m up over the middle of the ground.
        static readonly Matrix4x4 SlabModel =
            Matrix4x4.CreateScale(3f, 0.3f, 3f) * Matrix4x4.CreateTranslation(0f, 3f, 0f);

        // Key light tilted off vertical, so the slab's shadow lands well to -X of the slab itself and the two
        // sample points below can never be the same pixel.
        static readonly Vector3 KeyLight = Vector3.Normalize(new Vector3(-0.55f, -0.83f, 0f));

        static readonly Vector3 CameraTarget = Vector3.Zero;
        static readonly Vector3 CameraEye = new(7f, 6f, 7f);

        // Ground under the slab's shadow (the slab spans x in [-1.5, 1.5] at y 2.85, and the light drops that
        // footprint about 1.8 m toward -X), ground well clear of it, and the slab's own top-centre.
        static readonly Vector3 ShadowedGround = new(-1.8f, 0.1f, 0f);
        static readonly Vector3 LitGround = new(6f, 0.1f, 0f);
        static readonly Vector3 SlabTopCentre = new(0f, 3.15f, 0f);

        [GpuFact]
        public void A_shadow_only_slab_shades_the_ground_and_never_appears_on_it()
        {
            int baselineDrawn = -1, baselineShadowOnly = -1;
            byte[] baseline = Capture(withSlab: false, ref baselineDrawn, ref baselineShadowOnly);

            int drawn = -1, shadowOnly = -1;
            byte[] withSlab = Capture(withSlab: true, ref drawn, ref shadowOnly);

            Assert.True(Project(ShadowedGround, out int shadowX, out int shadowY), "the shadowed sample projected off-screen");
            Assert.True(Project(LitGround, out int litX, out int litY), "the lit sample projected off-screen");
            Assert.True(Project(SlabTopCentre, out int slabX, out int slabY), "the slab sample projected off-screen");
            // The framing guard: the slab's own pixel must not be its own shadow's pixel, or the second assertion
            // below would be measuring the first one.
            int dx = slabX - shadowX, dy = slabY - shadowY;
            Assert.True(dx * dx + dy * dy > 400,
                $"the slab projects to ({slabX},{slabY}) and its shadow to ({shadowX},{shadowY}), too close to tell apart");

            // It CAST: the ground under the slab is darker than the same ground with no slab in the scene, and
            // darker than lit ground in the same picture.
            int shadowed = Sample(withSlab, shadowX, shadowY);
            int shadowedBaseline = Sample(baseline, shadowX, shadowY);
            int lit = Sample(withSlab, litX, litY);
            Assert.True(shadowedBaseline > 60, $"the ground is too dark to measure a shadow on ({shadowedBaseline})");
            Assert.True(shadowed < shadowedBaseline - 15,
                $"no shadow under the slab: gray {shadowed} against {shadowedBaseline} with the slab left out. "
                + "A shadow-only instance must still record depth for the key light");
            Assert.True(shadowed < lit - 15,
                $"the shadowed ground ({shadowed}) is not darker than lit ground in the same picture ({lit})");

            // It DID NOT DRAW: the pixel it would have occupied is the baseline's. Had it leaked into the colour
            // pass it would be an untinted white slab face there instead of the grey ground behind it, which is
            // tens of levels away rather than the couple of levels of backend rounding this allows for.
            AssertPixelsMatch(withSlab, baseline, slabX, slabY);

            // And the frame's own counters agree with the picture: the slab is in neither the drawn nor the
            // culled total, it is in the shadow-only one, and the baseline frame has no shadow-only instance.
            Assert.Equal(baselineDrawn, drawn);
            Assert.Equal(0, baselineShadowOnly);
            Assert.Equal(1, shadowOnly);
        }

        // One capture of the scene, with or without the shadow-only slab. The counters are read at the top of the
        // SECOND frame, which is after the first frame rendered and before the second queues anything, so they are
        // that first frame's finalized totals.
        static byte[] Capture(bool withSlab, ref int drawn, ref int shadowOnly)
        {
            MeshHandle ground = default, slab = default;
            int frame = 0;
            int drawnSeen = -1, shadowOnlySeen = -1;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    ground = scene.LoadMesh(MeshPrimitives.Tile(24f, 0.1f));
                    slab = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = KeyLight;
                    scene.Camera.Frame(CameraTarget, CameraEye);
                },
                drawFrame: scene =>
                {
                    if (frame++ == 1)
                    {
                        drawnSeen = scene.DrawnInstances;
                        shadowOnlySeen = scene.ShadowOnlyInstances;
                    }
                    scene.Draw(ground, Matrix4x4.Identity, GroundGrey);
                    if (withSlab) scene.DrawShadowOnly(slab, SlabModel);
                },
                frames: 2);
            drawn = drawnSeen;
            shadowOnly = shadowOnlySeen;
            return rgba;
        }

        // Mean gray of a small window, so a one-pixel sampling difference cannot decide the test.
        static int Sample(byte[] rgba, int px, int py)
        {
            long sum = 0;
            int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    sum += (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3;
                    n++;
                }
            return n == 0 ? -1 : (int)(sum / n);
        }

        // Equal within a couple of levels, not byte-equal: the two captures rasterize the same ground pixel from
        // the same draw, so they agree, but a hair of backend rounding must not decide the test. A slab that drew
        // moves this pixel by tens of levels (measured 40 on Metal), so the tolerance costs the assertion nothing.
        const int PixelTolerance = 2;

        static void AssertPixelsMatch(byte[] a, byte[] b, int px, int py)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    for (int c = 0; c < 3; c++)
                        Assert.True(Math.Abs(a[i + c] - b[i + c]) <= PixelTolerance,
                            $"the shadow-only slab painted pixel ({x},{y}) channel {c}: {a[i + c]} against the "
                            + $"baseline's {b[i + c]}. A shadow-only instance must draw nothing in the colour pass");
                }
        }

        // Rebuild the capture camera and project, exactly as the blob-receiver test does. WorldToScreen returns a
        // top-left-origin y-down pixel, which is the readback buffer's own layout.
        static bool Project(Vector3 world, out int px, out int py)
        {
            var cam = new IsoCamera3D();
            cam.Frame(CameraTarget, CameraEye);
            cam.AspectRatio = (float)W / H;
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) { px = py = -1; return false; }
            px = (int)(p.X + 0.5f);
            py = (int)(p.Y + 0.5f);
            return true;
        }
    }
}
