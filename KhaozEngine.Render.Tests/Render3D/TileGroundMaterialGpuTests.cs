using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The tile-ground pipeline actually blends its four corner slots on a real device. A quad carries slots
    /// (0, 1, 0, 1) with the weights one-hot on slot 0 at its left corners and one-hot on slot 1 at its right, over
    /// a two-layer set of solid RED and solid BLUE, so a correct render reads red down the left, blue down the right
    /// and a mix in the middle. That is the whole design in one picture: the slots are constant across the triangle
    /// and the WEIGHTS are what vary, which is what makes a shared lattice corner sample one material at weight 1
    /// from every triangle touching it.
    /// <para>
    /// Tolerances, not a golden. The point is that the blend happened and picked the right layers, and a channel
    /// comparison says that without pinning the lighting, the tonemap or the post chain. The layers are SOLID
    /// colours on purpose, so the world-space tiling UV cannot affect what is sampled and the test says nothing
    /// about the tiling rate. Specular is turned off (baseSpecStrength 0) so nothing white is added on top of a
    /// pure-hue albedo.
    /// </para>
    /// <para>
    /// No <c>DisableParallelization</c> collection: each capture builds and drops its own device through
    /// <see cref="Render3DSnapshot"/> and touches no process-global state, which is what the other capture classes
    /// in this assembly do.
    /// </para>
    /// </summary>
    public sealed class TileGroundMaterialGpuTests
    {
        const int W = 192, H = 192;

        readonly ITestOutputHelper _out;
        public TileGroundMaterialGpuTests(ITestOutputHelper o) => _out = o;

        [GpuFact]
        public void Two_layer_set_blends_red_to_blue_across_the_quad()
        {
            byte[] rgba = Capture(scene =>
            {
                var mat = scene.LoadTileGroundMaterial(4, 4, new[]
                {
                    Layer(255, 0, 0),     // slot 0
                    Layer(0, 0, 255),     // slot 1
                }, baseSpecStrength: 0f);
                // Slots (0, 1, 0, 1) on every vertex, one-hot weights: slot 0 at the two left corners (world -X),
                // slot 1 at the two right corners. Weight index i selects slot index i, so w = (1,0,0,0) is slot 0.
                return scene.LoadMesh(Quad(new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), 0f, 1f), mat);
            });

            (int lr, int lg, int lb) = Patch(rgba, 0.18f, 0.5f);
            (int cr, int cg, int cb) = Patch(rgba, 0.50f, 0.5f);
            (int rr, int rg, int rb) = Patch(rgba, 0.82f, 0.5f);
            _out.WriteLine($"left=({lr},{lg},{lb}) centre=({cr},{cg},{cb}) right=({rr},{rg},{rb})");

            Assert.True(lr > lb + 40, $"the left of the quad is not red-dominant: ({lr},{lg},{lb})");
            Assert.True(rb > rr + 40, $"the right of the quad is not blue-dominant: ({rr},{rg},{rb})");
            Assert.True(cr > 20 && cb > 20, $"the centre carries neither layer: ({cr},{cg},{cb})");
            Assert.True(Math.Abs(cr - cb) < 45, $"the centre is not a mix of the two layers: ({cr},{cg},{cb})");
            // Neither layer has any green, so a green centre would mean the blend sampled something else entirely.
            Assert.True(cg < 40, $"the centre picked up a channel neither layer has: ({cr},{cg},{cb})");
        }

        [GpuFact]
        public void Single_flat_layer_reproduces_a_vertex_colour_look()
        {
            // The R1-to-R4 colour-only world drawn through the SAME pipeline: one flat layer, white tint, weight 1
            // everywhere. If this comes back anything but green the flat-fill fallback cannot replace vertex colour.
            byte[] rgba = Capture(scene =>
            {
                var mat = scene.LoadTileGroundMaterial(4, 4, new[] { Layer(0, 255, 0) }, baseSpecStrength: 0f);
                var one = new Vector4(1f, 0f, 0f, 0f);
                return scene.LoadMesh(Quad(one, one, 0f, 0f), mat);
            });

            (int r, int g, int b) = Patch(rgba, 0.50f, 0.5f);
            _out.WriteLine($"flat centre=({r},{g},{b})");
            Assert.True(g > 60, $"the flat layer did not render: ({r},{g},{b})");
            Assert.True(g > r + 40 && g > b + 40, $"the flat layer is not green-dominant: ({r},{g},{b})");
        }

        // ---- helpers -------------------------------------------------------------------------------------

        // A straight-down-ish orthographic view of the quad, so screen +x is world +x and the left/right halves of
        // the image are the left/right halves of the quad. Elevation stops just short of vertical because a look-at
        // straight down the up axis is degenerate.
        static byte[] Capture(Func<Scene3D, MeshHandle> setup)
        {
            MeshHandle h = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    h = setup(scene);
                    scene.Camera.Azimuth = 0f;
                    scene.Camera.Elevation = 1.5f;
                    scene.Camera.Target = Vector3.Zero;
                    scene.Camera.AspectRatio = (float)W / H;
                    scene.Camera.Zoom = 1f;
                    scene.Camera.OrthoSize = 5f;   // the quad is 4 units across, so it fills 80% of the viewport
                },
                drawFrame: scene => scene.Draw(h, Matrix4x4.Identity));
        }

        // A 4x4 XZ quad at y = 0, two triangles. Every vertex carries the same four slots (slot0, slot1, slot0,
        // slot1) and jitter 1, and the two LEFT (world -X) corners take leftWeights while the two right take
        // rightWeights, so the fragment interpolates from one to the other across the quad.
        static GltfMesh Quad(Vector4 leftWeights, Vector4 rightWeights, float slot0, float slot1)
        {
            var normal = new Vector3(0f, 1f, 0f);
            Vector2 uv = new(slot0, slot1);                       // slots 0 and 1
            Vector4 tangent = new(slot0, slot1, 1f, 0f);          // slots 2 and 3, then the jitter, then 0
            var verts = new[]
            {
                new ModelVertex(new Vector3(-2f, 0f, -2f), normal, leftWeights, uv, tangent),
                new ModelVertex(new Vector3(2f, 0f, -2f), normal, rightWeights, uv, tangent),
                new ModelVertex(new Vector3(2f, 0f, 2f), normal, rightWeights, uv, tangent),
                new ModelVertex(new Vector3(-2f, 0f, 2f), normal, leftWeights, uv, tangent),
            };
            return new GltfMesh(verts, new ushort[] { 0, 1, 2, 0, 2, 3 });
        }

        static TileGroundLayerImage Layer(byte r, byte g, byte b)
        {
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255; }
            return new TileGroundLayerImage { AlbedoRgba = px, TilesPerMetre = 0.5f };
        }

        // Mean RGB of a small patch around the fractional image position, so one stray texel cannot decide the test.
        static (int R, int G, int B) Patch(byte[] rgba, float fx, float fy)
        {
            int cx = (int)(fx * W), cy = (int)(fy * H);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int y = cy - 3; y <= cy + 3; y++)
                for (int x = cx - 3; x <= cx + 3; x++)
                {
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                }
            return ((int)(r / n), (int)(g / n), (int)(b / n));
        }
    }
}
