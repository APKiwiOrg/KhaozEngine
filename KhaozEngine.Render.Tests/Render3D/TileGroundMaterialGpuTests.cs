using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The tile-ground pipeline actually blends its four corner slots on a real device. A quad carries slots
    /// (0, 1, 2, 3) over a four-layer set of solid RED, BLUE, GREEN and WHITE, and is captured twice: once with the
    /// weights one-hot on lanes 2 and 3 (so a correct render is green down the left and white down the right) and
    /// once one-hot on lanes 0 and 1 (red left, blue right). That is the whole design in one picture: the slots are
    /// constant across the triangle and the WEIGHTS are what vary, which is what makes a shared lattice corner
    /// sample one material at weight 1 from every triangle touching it.
    /// <para>
    /// FOUR DISTINCT LAYERS AND BOTH LANE PAIRS, on purpose. A shader that swapped the <c>Uv</c> pair with the
    /// <c>Tangent</c> pair would still blend two materials and still read left to right, so a two-layer quad would
    /// pass it. With four, the swap turns the first capture red-and-blue instead of green-and-white and the second
    /// one the other way round, and both go red. The declared packing is ALSO pinned as a plain string assertion
    /// below, which runs on every leg and is the contract the tile-world mesher writes against.
    /// </para>
    /// <para>
    /// Tolerances, not a golden. The point is that the blend happened and picked the right layers, and a channel
    /// comparison says that without pinning the lighting, the tonemap or the post chain. The layers are SOLID
    /// colours on purpose, so the world-space tiling UV cannot affect what is sampled and the test says nothing
    /// about the tiling rate. Specular is turned off (baseSpecStrength 0) so no white highlight can be mistaken
    /// for the white LAYER.
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
        public void Four_slot_lanes_each_select_the_layer_their_own_vertex_field_names()
        {
            // Lanes 2 and 3, which come from Tangent.xy: green at the left corners, white at the right. A shader
            // reading the Uv pair for these would render red and blue instead.
            byte[] tangentLanes = Capture(scene => FourLayerQuad(
                scene, new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 0f, 0f, 1f)));

            (int lr, int lg, int lb) = Patch(tangentLanes, 0.18f, 0.5f);
            (int cr, int cg, int cb) = Patch(tangentLanes, 0.50f, 0.5f);
            (int rr, int rg, int rb) = Patch(tangentLanes, 0.82f, 0.5f);
            _out.WriteLine($"tangent lanes: left=({lr},{lg},{lb}) centre=({cr},{cg},{cb}) right=({rr},{rg},{rb})");

            Assert.True(lg > lr + 40 && lg > lb + 40,
                $"lane 2 did not select the GREEN layer Tangent.x names: ({lr},{lg},{lb})");
            Assert.True(rr > 120 && rg > 120 && rb > 120,
                $"lane 3 did not select the WHITE layer Tangent.y names: ({rr},{rg},{rb})");
            Assert.True(cg > 60 && cr > 20 && cb > 20, $"the centre blends neither layer: ({cr},{cg},{cb})");

            // Lanes 0 and 1, which come from Uv.xy: red at the left corners, blue at the right. The same quad and
            // the same set, so only the weights moved, which is what makes the pair a swap test and not two facts.
            byte[] uvLanes = Capture(scene => FourLayerQuad(
                scene, new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f)));

            (lr, lg, lb) = Patch(uvLanes, 0.18f, 0.5f);
            (cr, cg, cb) = Patch(uvLanes, 0.50f, 0.5f);
            (rr, rg, rb) = Patch(uvLanes, 0.82f, 0.5f);
            _out.WriteLine($"uv lanes: left=({lr},{lg},{lb}) centre=({cr},{cg},{cb}) right=({rr},{rg},{rb})");

            Assert.True(lr > lg + 40 && lr > lb + 40,
                $"lane 0 did not select the RED layer Uv.x names: ({lr},{lg},{lb})");
            Assert.True(rb > rr + 40 && rb > rg + 40,
                $"lane 1 did not select the BLUE layer Uv.y names: ({rr},{rg},{rb})");
            Assert.True(cr > 20 && cb > 20, $"the centre carries neither layer: ({cr},{cg},{cb})");
            Assert.True(Math.Abs(cr - cb) < 45, $"the centre is not a mix of the two layers: ({cr},{cg},{cb})");
            // Neither of those two layers has any green, so green at the centre means the blend read elsewhere.
            Assert.True(cg < 40, $"the centre picked up a channel neither layer has: ({cr},{cg},{cb})");
        }

        /// <summary>
        /// THE PACKING, PINNED AS TEXT, so the tile-world mesher cannot silently disagree with the shader. A plain
        /// [Fact] rather than a [GpuFact] on purpose: this is the half of the contract that should go red on every
        /// leg, including the ones with no device, the moment either side moves. The GPU fact above proves the
        /// packing WORKS, this proves it is still the packing the mesher is written against.
        /// </summary>
        [Fact]
        public void TheVertexPacking_IsSlotsInUvAndTangentXy_WithJitterInTangentZ()
        {
            Assert.Contains("vSlots = vec4(TexCoord.x, TexCoord.y, Tangent.x, Tangent.y);", ShaderSources.TileGroundVert);
            Assert.Contains("vJitter = Tangent.z;", ShaderSources.TileGroundVert);
            Assert.Contains("vWeights = Color;", ShaderSources.TileGroundVert);
        }

        /// <summary>
        /// AND THIS ONE IS THE ONE-LAYER ARRAY CONFORMANCE DRAW (#666). The set has exactly one layer, so the
        /// albedo texture is a texture ARRAY with a single slice, bound under a fragment that declares
        /// <c>texture2DArray</c>. Until the seam could say "array of one" this test only passed because
        /// <c>Scene3D.LoadTileGroundMaterial</c> padded the set to two layers by duplicating it: one layer created
        /// a plain 2D texture, and Metal validation killed the test host at this very draw
        /// (<c>incorrect type of texture (MTLTextureType2D) bound ... (expect MTLTextureType2DArray)</c>) while
        /// lavapipe rendered through it silently. The pad is gone, so what runs here now is the real thing, on
        /// every backend the golden matrix covers: Metal and metal-native, WARP and d3d11-native, lavapipe and
        /// vulkan-native.
        /// </summary>
        [GpuFact]
        public void Single_flat_layer_reproduces_a_vertex_colour_look()
        {
            // The R1-to-R4 colour-only world drawn through the SAME pipeline: one flat layer, white tint, weight 1
            // everywhere. If this comes back anything but green the flat-fill fallback cannot replace vertex colour.
            byte[] rgba = Capture(scene =>
            {
                var mat = scene.LoadTileGroundMaterial(4, 4, new[] { Layer(0, 255, 0) }, baseSpecStrength: 0f);
                var one = new Vector4(1f, 0f, 0f, 0f);
                return scene.LoadMesh(Quad(one, one, 0f, 0f, 0f, 0f), mat);
            });

            (int r, int g, int b) = Patch(rgba, 0.50f, 0.5f);
            _out.WriteLine($"flat centre=({r},{g},{b})");
            Assert.True(g > 60, $"the flat layer did not render: ({r},{g},{b})");
            Assert.True(g > r + 40 && g > b + 40, $"the flat layer is not green-dominant: ({r},{g},{b})");
        }

        // ---- helpers -------------------------------------------------------------------------------------

        // The four-layer set and the quad both captures share: slots (0, 1, 2, 3) on every vertex over layers red,
        // blue, green and white, with the caller choosing which weight lane is one-hot at the left and right corners.
        static MeshHandle FourLayerQuad(Scene3D scene, Vector4 leftWeights, Vector4 rightWeights)
        {
            var mat = scene.LoadTileGroundMaterial(4, 4, new[]
            {
                Layer(255, 0, 0),         // slot 0, named by Uv.x
                Layer(0, 0, 255),         // slot 1, named by Uv.y
                Layer(0, 255, 0),         // slot 2, named by Tangent.x
                Layer(255, 255, 255),     // slot 3, named by Tangent.y
            }, baseSpecStrength: 0f);
            return scene.LoadMesh(Quad(leftWeights, rightWeights, 0f, 1f, 2f, 3f), mat);
        }

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

        // A 4x4 XZ quad at y = 0, two triangles. Every vertex carries the same four slots and jitter 1, and the
        // two LEFT (world -X) corners take leftWeights while the two right take rightWeights, so the fragment
        // interpolates from one to the other across the quad. Jitter is 1 rather than 0 because the shader
        // MULTIPLIES by it, and a 0 there renders the whole quad black (the trap the LoadMesh doc names).
        static GltfMesh Quad(Vector4 leftWeights, Vector4 rightWeights,
            float slot0, float slot1, float slot2, float slot3)
        {
            var normal = new Vector3(0f, 1f, 0f);
            Vector2 uv = new(slot0, slot1);                       // slots 0 and 1
            Vector4 tangent = new(slot2, slot3, 1f, 0f);          // slots 2 and 3, then the jitter, then 0
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
