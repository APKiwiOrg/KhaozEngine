using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Committed-grid goldens for the splat terrain pass, complementing the threshold-invariant checks in
    /// <c>KhaozEngine.Tests.Terrain.SplatTerrainGoldenTests</c> / <c>SplatTerrainDistanceGoldenTests</c>. Those
    /// invariant tests encode intent a grid cannot (textured-not-white, lit-at-distance) and stay untouched; this
    /// file pins the actual rendered pixels of the SAME two deterministic scenes to a per-backend reference grid
    /// via <see cref="GoldenCompare.AssertOrUpdate(string,byte[],int,int)"/>. Splat terrain is the most
    /// regression-prone shader in the engine (the flat-white FXC interpolant-gap bug shipped precisely because
    /// only invariant checks existed and no committed grid pinned its output), so it now gets committed grids like
    /// every other scene.
    /// <para>
    /// Both scenes are deterministic: fixed camera, procedural <see cref="TerrainPresets.Clearing"/> field (no
    /// RNG, no wall-clock), fixed geometry and material. Rendered at the standard golden dimensions
    /// <see cref="W"/>x<see cref="H"/>. Class + method names carry "Golden" so the cross-platform GPU matrix
    /// (<c>--filter FullyQualifiedName~Golden</c>) runs them on every backend.
    /// </para>
    /// </summary>
    public sealed class SplatTerrainGridGoldenTests
    {
        const int W = 480, H = 320;

        // Near/standard splat view: the primary scene from SplatTerrainGoldenTests (procedural grass/dirt Clearing
        // terrain, single chunk, near-top-down iso frame). Mirrors that test's determinism exactly, only the
        // capture dimensions differ (its invariant version uses 96x96).
        static byte[] CaptureNearSplat()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var chunk = TerrainChunkBuilder.Build(
                field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f }, lod: 0);

            MeshHandle h = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
                    h = scene.LoadTerrainChunk(chunk, mat);
                    scene.Camera.Frame(new Vector3(16f, 1f, 16f), new Vector3(16f, 26f, 16.4f));
                },
                drawFrame: scene => scene.DrawTerrainChunk(h));
        }

        // Distance/mip view: the grazing perspective camera over a receding strip of chunks textured with the
        // high-frequency checkerboard albedo, from SplatTerrainDistanceGoldenTests. Same field, regions, material,
        // and camera as that test (its invariant version uses 160x120).
        static byte[] CaptureDistanceSplat()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var material = CheckerboardMaterial(size: 64, cell: 4, tilesPerMetre: 0.2f);

            const float size = TerrainChunkRegion.DefaultSize;   // 60 m
            var regions = new List<TerrainChunkRegion>();
            for (int cz = 0; cz < 5; cz++)
                for (int cx = -1; cx <= 1; cx++)
                    regions.Add(new TerrainChunkRegion { OriginX = cx * size, OriginZ = cz * size, Size = size });

            var handles = new List<MeshHandle>();
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(material);
                    foreach (var r in regions)
                    {
                        var chunk = TerrainChunkBuilder.Build(field, r, lod: 1);
                        handles.Add(scene.LoadTerrainChunk(chunk, mat));
                    }

                    var cam = new FollowCamera3D
                    {
                        Target = new Vector3(0f, 0.5f, 10f),
                        Yaw = MathF.PI,
                        AspectRatio = (float)W / H,
                        FarPlane = 800f,
                        MaxDistance = 30f,
                        HeightOffset = 1.2f,
                    };
                    cam.Distance = 28f;
                    cam.Pitch = cam.MinPitch;   // ~6 deg above horizontal: a grazing look across the ground
                    scene.CameraOverride = cam;
                },
                drawFrame: scene => { foreach (var h in handles) scene.DrawTerrainChunk(h); });
        }

        [GpuFact]
        public void SplatTerrainNearGolden_GridMatchesReference()
        {
            byte[] rgba = CaptureNearSplat();
            GoldenCompare.AssertOrUpdate("scene3d_splat", rgba, W, H);
        }

        [GpuFact]
        public void SplatTerrainDistanceGolden_GridMatchesReference()
        {
            byte[] rgba = CaptureDistanceSplat();
            GoldenCompare.AssertOrUpdate("scene3d_splat_distance", rgba, W, H);
        }

        // Two contrasting mid-tone checker cells (same as SplatTerrainDistanceGoldenTests): both clearly
        // non-background and non-white, tiled at high frequency so the distance pass must mip/aniso filter.
        static readonly Color CheckerA = new Color(60 / 255f, 90 / 255f, 40 / 255f);
        static readonly Color CheckerB = new Color(180 / 255f, 170 / 255f, 120 / 255f);

        // A five-layer material whose every layer is the same high-frequency checkerboard albedo, so the rendered
        // ground is the checker regardless of the per-vertex splat blend. Flat tangent-space normal per texel.
        static TerrainLayeredMaterial CheckerboardMaterial(int size, int cell, float tilesPerMetre)
        {
            byte[] albedo = new byte[size * size * 4];
            byte[] normal = new byte[size * size * 4];
            static byte U(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    bool onA = (((x / cell) + (y / cell)) & 1) == 0;
                    Color c = onA ? CheckerA : CheckerB;
                    albedo[i + 0] = U(c.R); albedo[i + 1] = U(c.G); albedo[i + 2] = U(c.B); albedo[i + 3] = 255;
                    normal[i + 0] = 128; normal[i + 1] = 128; normal[i + 2] = 255; normal[i + 3] = 255;   // tangent-space up
                }

            TerrainMaterialLayer Layer() => new()
            {
                AlbedoRgba = (byte[])albedo.Clone(),
                NormalRgba = (byte[])normal.Clone(),
                Tint = Color.White,
                TilesPerMetre = tilesPerMetre,
                Roughness = 0.9f,
            };

            return new TerrainLayeredMaterial
            {
                Width = size, Height = size,
                Grass = Layer(), Dirt = Layer(), Rock = Layer(), Sand = Layer(), Snow = Layer(),
            };
        }
    }
}
