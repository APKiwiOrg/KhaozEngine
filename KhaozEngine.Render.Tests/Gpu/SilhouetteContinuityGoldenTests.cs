using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Pixel invariants for welded per-target outlines. The faceted tree and narrow multipart body catch gaps at
    /// split normals. The wall in front of the third target catches a hull that ignores scene depth.
    /// </summary>
    public sealed class SilhouetteContinuityGoldenTests
    {
        const int W = 720, H = 400;
        static readonly Color Target = new(0.16f, 0.19f, 0.24f, 1f);
        static readonly Color Rim = new(1f, 0.78f, 0.04f, 1f);
        static readonly Color Wall = new(0.05f, 0.35f, 0.95f, 1f);

        [GpuFact]
        public void Golden3D_Silhouette_WeldedFacetsStayContinuousAndSceneDepthOccludesThem()
        {
            IReadOnlyList<MeshHandle>? tree = null;
            MeshHandle body = default, hiddenBox = default, wall = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.025f, 0.03f, 0.045f, 1f);
                    scene.Camera.Frame(new Vector3(0f, 1.2f, 0f), new Vector3(0f, 1.2f, 11f));
                    tree = scene.LoadPropMeshes(SplitTriangles(TreeLikeMesh()));
                    body = scene.LoadMesh(NarrowBodyMesh());
                    hiddenBox = scene.LoadMesh(MeshPrimitives.Box(1f));
                    wall = scene.LoadMesh(MeshPrimitives.Box(1f));
                },
                drawFrame: scene =>
                {
                    foreach (MeshHandle part in tree!) DrawTarget(scene, part, Matrix4x4.Identity);
                    DrawTarget(scene, body, Matrix4x4.Identity);
                    DrawTarget(scene, hiddenBox,
                        Matrix4x4.CreateScale(1.15f, 1.7f, 0.7f) * Matrix4x4.CreateTranslation(2.5f, 1.05f, 0f));
                    scene.Draw(wall,
                        Matrix4x4.CreateScale(0.52f, 2.2f, 0.35f) * Matrix4x4.CreateTranslation(2.5f, 1.05f, 1f),
                        Wall, Material.Glowing(Wall));
                },
                frames: 2);

            string directory = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR")
                ?? Path.Combine(Path.GetTempPath(), "khaoz-outline-evidence");
            Directory.CreateDirectory(directory);
            string artifact = Path.Combine(directory, "silhouette_continuity.png");
            PngWriter.Save(artifact, rgba, W, H);
            Console.WriteLine($"Silhouette evidence: {artifact}");

            AssertContinuousSparseRim(rgba, 0, 315, 0.82f, "faceted tree");
            AssertContinuousSparseRim(rgba, 300, 420, 0.64f, "narrow multipart body");
            AssertWallOccludesRim(rgba, 400, W);
        }

        static void DrawTarget(Scene3D scene, MeshHandle mesh, Matrix4x4 world)
        {
            scene.Draw(mesh, world, Target, Material.Glowing(Target));
            scene.DrawMeshSilhouette(mesh, world, Rim, 0.075f);
        }

        static GltfMesh TreeLikeMesh()
        {
            var builder = new MeshBuilder();
            builder.Add(MeshPrimitives.Box(1f),
                Matrix4x4.CreateScale(0.42f, 1.45f, 0.42f) * Matrix4x4.CreateTranslation(-2.5f, 0.725f, 0f));
            builder.Add(MeshPrimitives.Pyramid(1.65f, 1.45f),
                Matrix4x4.CreateTranslation(-2.5f, 1.15f, 0f));
            builder.Add(MeshPrimitives.Pyramid(1.3f, 1.15f),
                Matrix4x4.CreateRotationY(0.55f) * Matrix4x4.CreateTranslation(-2.5f, 1.75f, 0f));
            return builder.Build();
        }

        static GltfMesh NarrowBodyMesh()
        {
            var builder = new MeshBuilder();
            GltfMesh box = MeshPrimitives.Box(1f);
            builder.Add(box, Part(0f, 1.2f, 0.65f, 1f, 0.35f));
            builder.Add(box, Part(0f, 1.925f, 0.45f, 0.45f, 0.4f));
            builder.Add(box, Part(-0.435f, 1.25f, 0.22f, 0.9f, 0.25f));
            builder.Add(box, Part(0.435f, 1.25f, 0.22f, 0.9f, 0.25f));
            builder.Add(box, Part(-0.175f, 0.3f, 0.25f, 0.8f, 0.3f));
            builder.Add(box, Part(0.175f, 0.3f, 0.25f, 0.8f, 0.3f));
            return builder.Build();
        }

        static IReadOnlyList<GltfMeshPart> SplitTriangles(GltfMesh mesh)
        {
            var parts = new List<GltfMeshPart>(mesh.TriangleCount);
            for (int i = 0; i + 2 < mesh.Indices32.Length; i += 3)
            {
                var vertices = new ModelVertex[3];
                vertices[0] = mesh.Vertices[mesh.Indices32[i]];
                vertices[1] = mesh.Vertices[mesh.Indices32[i + 1]];
                vertices[2] = mesh.Vertices[mesh.Indices32[i + 2]];
                parts.Add(new GltfMeshPart(new GltfMesh(vertices, new uint[] { 0, 1, 2 }), default));
            }
            return parts;
        }

        static Matrix4x4 Part(float x, float y, float sx, float sy, float sz)
            => Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateTranslation(x, y, 0f);

        static void AssertContinuousSparseRim(byte[] rgba, int minX, int maxX, float minimumConnectedFraction,
            string name)
        {
            bool[] mask = Mask(rgba, minX, maxX, IsRim);
            int total = Count(mask);
            int largest = LargestComponent(mask, maxX - minX, H);
            Assert.True(total > 80, $"{name} drew only {total} rim pixels");
            Assert.True(largest >= total * minimumConnectedFraction,
                $"{name} rim split into pieces, largest component {largest} of {total} pixels");
            Assert.True(total < (maxX - minX) * H * 0.08f,
                $"{name} filled its interior with {total} rim pixels");
        }

        static void AssertWallOccludesRim(byte[] rgba, int minX, int maxX)
        {
            Bounds wall = FindBounds(rgba, minX, maxX, IsWall);
            Assert.True(wall.Count > 500, $"near wall drew only {wall.Count} blue pixels");
            int hiddenRim = 0;
            for (int y = wall.MinY + 4; y <= wall.MaxY - 4; y++)
                for (int x = wall.MinX + 4; x <= wall.MaxX - 4; x++)
                    if (IsRim(rgba, (y * W + x) * 4)) hiddenRim++;
            Assert.Equal(0, hiddenRim);

            int visibleRim = 0;
            for (int y = 0; y < H; y++)
                for (int x = minX; x < maxX; x++)
                    if (IsRim(rgba, (y * W + x) * 4)) visibleRim++;
            Assert.True(visibleRim > 30, $"occluded target lost its visible outer rim, pixels {visibleRim}");
        }

        static bool[] Mask(byte[] rgba, int minX, int maxX, Func<byte[], int, bool> predicate)
        {
            int width = maxX - minX;
            var mask = new bool[width * H];
            for (int y = 0; y < H; y++)
                for (int x = minX; x < maxX; x++)
                    mask[y * width + x - minX] = predicate(rgba, (y * W + x) * 4);
            return mask;
        }

        static int Count(bool[] mask)
        {
            int count = 0;
            foreach (bool value in mask) if (value) count++;
            return count;
        }

        static int LargestComponent(bool[] mask, int width, int height)
        {
            int largest = 0;
            var queue = new Queue<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start]) continue;
                mask[start] = false;
                queue.Enqueue(start);
                int count = 0;
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    count++;
                    int x = current % width, y = current / width;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                            int next = ny * width + nx;
                            if (!mask[next]) continue;
                            mask[next] = false;
                            queue.Enqueue(next);
                        }
                }
                if (count > largest) largest = count;
            }
            return largest;
        }

        static Bounds FindBounds(byte[] rgba, int minX, int maxX, Func<byte[], int, bool> predicate)
        {
            var bounds = new Bounds(W, H, -1, -1, 0);
            for (int y = 0; y < H; y++)
                for (int x = minX; x < maxX; x++)
                {
                    if (!predicate(rgba, (y * W + x) * 4)) continue;
                    bounds = new Bounds(Math.Min(bounds.MinX, x), Math.Min(bounds.MinY, y),
                        Math.Max(bounds.MaxX, x), Math.Max(bounds.MaxY, y), bounds.Count + 1);
                }
            return bounds;
        }

        static bool IsRim(byte[] rgba, int i)
            => rgba[i] > 180 && rgba[i + 1] > 105 && rgba[i + 2] < 80;

        static bool IsWall(byte[] rgba, int i)
            => rgba[i + 2] > 150 && rgba[i + 2] > rgba[i] + 60 && rgba[i + 2] > rgba[i + 1] + 40;

        readonly record struct Bounds(int MinX, int MinY, int MaxX, int MaxY, int Count);
    }
}
