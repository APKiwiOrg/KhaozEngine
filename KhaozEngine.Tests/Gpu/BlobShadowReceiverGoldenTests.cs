using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Blob shadows must be GROUND-RECEIVER-ONLY: a caster's own body must never be repainted by its blob. The blob
    /// decal paints every pixel in the world-Y band <c>groundY - YTolerance .. groundY + MaxStep</c> inside the disc,
    /// which includes the lower ~0.4 m of the caster's own mesh. Drawing the blob decals BEFORE the skinned character
    /// pass makes the character opaquely occlude its own blob, so the band never lands on the body. This renders a
    /// tall bright-green SKINNED box (the character path - a rigid mesh is a receiver and would legitimately take the
    /// blob) standing on a floor with a blob under it, and asserts via the camera projection that the box's lower face
    /// (inside the blob band) is NOT darker than its upper face (above the band) - the pre-fix draw order darkened the
    /// lower face. Backend-independent (asserts a same-face brightness ratio, not committed pixels); the "Golden" in
    /// the name enrols it in the cross-backend GPU matrix. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class BlobShadowReceiverGoldenTests
    {
        const int W = 360, H = 360;

        // Box: a thin tall column standing on the floor (base at y=0, top at y=2.4, footprint 0.7). Scale a unit box
        // then lift it so its base sits on the ground plane. The +Z face is at world z=0.35.
        static readonly Matrix4x4 BoxModel =
            Matrix4x4.CreateScale(0.7f, 2.4f, 0.7f) * Matrix4x4.CreateTranslation(0f, 1.2f, 0f);
        static readonly Color BoxGreen = new(0.12f, 0.9f, 0.18f, 1f);
        static readonly Matrix4x4[] RestPose = { Matrix4x4.Identity };

        [GpuFact]
        public void Blob_shadow_is_not_painted_on_its_casters_body()
        {
            MeshHandle floor = default;
            SkinnedMeshHandle box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    box = scene.LoadSkinnedMesh(BuildSkinnedBox());
                    scene.Post.Starfield = false;
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
                    // Look down at the box from +X/+Z so its +Z face (sampled below) is front-facing and unoccluded.
                    scene.Camera.Frame(new Vector3(0f, 1.0f, 0f), new Vector3(6f, 5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity);
                    // The caster is a SKINNED mesh at rest - the real character path (ReplicatedCharacterAnimators
                    // draws skinned). The blob under it spills onto the floor beyond the 0.7 footprint. Defaults give
                    // the band world-Y in [-0.3, 0.4], so the box's lower face (y in [0, 0.4]) sits inside it.
                    scene.DrawSkinned(box, RestPose, BoxModel, BoxGreen);
                    scene.AddShadowBlob(new ShadowBlob(new Vector3(0f, 0f, 0f), groundY: 0f, radius: 1.1f));
                },
                frames: 2);

            // Sample the box's +Z face (x=0, z=0.35) at low Y (inside the band) and high Y (above it). Same face =>
            // same shading, so absent the blob the two bands are equally bright; the pre-fix blob darkens only the low.
            var lowGreens = new List<int>();
            var highGreens = new List<int>();
            foreach (float y in new[] { 0.10f, 0.20f, 0.30f })
                CollectGreens(rgba, new Vector3(0f, y, 0.35f), lowGreens);
            foreach (float y in new[] { 1.30f, 1.60f, 1.90f })
                CollectGreens(rgba, new Vector3(0f, y, 0.35f), highGreens);

            Assert.True(lowGreens.Count >= 20, $"low band sampled too few box pixels ({lowGreens.Count})");
            Assert.True(highGreens.Count >= 20, $"high band sampled too few box pixels ({highGreens.Count})");

            double lowMean = Mean(lowGreens);
            double highMean = Mean(highGreens);
            Assert.True(highMean > 60, $"box face not bright enough to test ({highMean:F0})");
            Assert.True(lowMean >= 0.80 * highMean,
                $"blob repainted the caster's lower body: low-face green {lowMean:F0} is darker than upper-face {highMean:F0} " +
                $"(ratio {lowMean / highMean:F2}); blob decals must draw before the character pass");

            // Sanity: the blob still renders on the floor (a near-floor point inside the disc is darker than a far one
            // outside it). Confirms the reorder did not simply drop the blob.
            int nearFloor = SampleGray(rgba, new Vector3(0.85f, 0.02f, 0.0f));
            int farFloor = SampleGray(rgba, new Vector3(4.0f, 0.02f, 4.0f));
            Assert.True(nearFloor >= 0 && farFloor >= 0, "floor sample points projected off-screen");
            Assert.True(nearFloor < farFloor - 15,
                $"blob missing on the floor: under-caster gray {nearFloor} not darker than far-floor {farFloor}");
        }

        // Project a world point through the SAME camera the capture used (rebuilt identically here) and pool the green
        // channel of every green-dominant (box) pixel in a small window around it. Green-dominance filters out the
        // grey floor / blob so only box pixels count, even if a window straddles the box silhouette edge.
        static void CollectGreens(byte[] rgba, Vector3 world, List<int> into)
        {
            if (!Project(world, out int px, out int py)) return;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                    if (g > 40 && g > r + 25 && g > b + 25) into.Add(g);
                }
        }

        static int SampleGray(byte[] rgba, Vector3 world)
        {
            if (!Project(world, out int px, out int py)) return -1;
            long sum = 0; int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    sum += (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3; n++;
                }
            return n == 0 ? -1 : (int)(sum / n);
        }

        // Rebuild the capture camera and project. WorldToScreen returns a top-left-origin, y-down pixel, matching the
        // GpuReadback row-major/top-left buffer, so the pixel indexes the readback directly.
        static bool Project(Vector3 world, out int px, out int py)
        {
            var cam = new IsoCamera3D();
            cam.Frame(new Vector3(0f, 1.0f, 0f), new Vector3(6f, 5f, 6f));
            cam.AspectRatio = (float)W / H;   // Scene3D sets this from the viewport at render time (after Frame).
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) { px = py = -1; return false; }
            px = (int)(p.X + 0.5f); py = (int)(p.Y + 0.5f);
            return true;
        }

        static double Mean(List<int> v)
        {
            long s = 0;
            foreach (int x in v) s += x;
            return v.Count == 0 ? 0 : (double)s / v.Count;
        }

        // A single-bone unit box (edge 1, centred at origin), all vertices weighted to bone 0 with identity bind/rest
        // so DrawSkinned renders it undeformed - a stand-in character on the CPU-skinned path. Flat per-face normals
        // keep each face uniformly lit along its height, so the low-vs-high face comparison isolates the blob. Model
        // cull is None, so triangle winding is irrelevant.
        static SkinnedGltfMesh BuildSkinnedBox()
        {
            var verts = new List<SkinnedVertex>();
            var idx = new List<ushort>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
            {
                ushort baseIdx = (ushort)verts.Count;
                foreach (Vector3 p in new[] { a, b, c, d })
                    verts.Add(new SkinnedVertex
                    {
                        Position = p,
                        Normal = n,
                        Color = new Vector4(1f, 1f, 1f, 1f),
                        Uv = Vector2.Zero,
                        BoneIndices = Vector4.Zero,
                        BoneWeights = new Vector4(1f, 0f, 0f, 0f),
                        Tangent = Vector4.Zero,
                    });
                idx.Add(baseIdx); idx.Add((ushort)(baseIdx + 1)); idx.Add((ushort)(baseIdx + 2));
                idx.Add(baseIdx); idx.Add((ushort)(baseIdx + 2)); idx.Add((ushort)(baseIdx + 3));
            }

            const float h = 0.5f;
            Quad(new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h), new(0, 0, 1));   // +Z
            Quad(new(h, -h, -h), new(-h, -h, -h), new(-h, h, -h), new(h, h, -h), new(0, 0, -1)); // -Z
            Quad(new(h, -h, h), new(h, -h, -h), new(h, h, -h), new(h, h, h), new(1, 0, 0));   // +X
            Quad(new(-h, -h, -h), new(-h, -h, h), new(-h, h, h), new(-h, h, -h), new(-1, 0, 0)); // -X
            Quad(new(-h, h, h), new(h, h, h), new(h, h, -h), new(-h, h, -h), new(0, 1, 0));   // +Y
            Quad(new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h), new(0, -1, 0)); // -Y

            var bones = new[] { Matrix4x4.Identity };
            return new SkinnedGltfMesh(verts.ToArray(), idx.ToArray(), bones, bones);
        }
    }
}
