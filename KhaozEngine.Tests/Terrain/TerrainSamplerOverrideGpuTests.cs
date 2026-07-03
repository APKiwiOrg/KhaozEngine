using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // A terrain material with a per-material sampler override still renders the textured ground end to end on a real
    // device: the override path builds + binds an OWNED sampler (trilinear + no anisotropy + a big mip bias here)
    // instead of the renderer's shared default. Not "Golden"-named, so this runs on the local Metal device only, not
    // the CI backend legs (the sampler value that actually cures the distance fuzz is confirmed by the game A/B, not
    // pinned here).
    public sealed class TerrainSamplerOverrideGpuTests
    {
        [GpuFact]
        public void SplatMaterialWithSamplerOverrideRendersTextured()
        {
            const int W = 96, H = 96;
            var field = new TerrainField(TerrainPresets.Clearing());
            var chunk = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f }, lod: 0);

            var material = TerrainMaterialPresets.Procedural(32);
            material.Sampler = new TerrainSamplerConfig(GpuSamplerFilter.MinLinearMagLinearMipLinear, maximumAnisotropy: 1, mipLodBias: 4);

            MeshHandle h = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(material);
                    h = scene.LoadTerrainChunk(chunk, mat);
                    scene.Camera.Frame(new Vector3(16f, 1f, 16f), new Vector3(16f, 26f, 16.4f));
                },
                drawFrame: scene => scene.DrawTerrainChunk(h));

            // Centre must be a lit, tinted texture, not background/black or blown-out white - i.e. the override
            // sampler bound + sampled the arrays. (Trilinear + bias 4 heavily blurs it, so we do NOT require a high
            // channel spread the way the aniso golden does.)
            int i = (H / 2 * W + W / 2) * 4;
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            Assert.True(r + g + b > 60, $"terrain centre is background/black ({r},{g},{b}) - override sampler did not render.");
            Assert.False(r >= 235 && g >= 235 && b >= 235, $"terrain centre is near-white ({r},{g},{b}).");
        }
    }
}
