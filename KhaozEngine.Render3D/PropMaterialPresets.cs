using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Procedural placeholder prop materials so the in-repo sample and tests show a textured prop without
    /// shipping binary textures. Real games supply a textured glTF (baseColor/normal) read via
    /// <see cref="PropLoader.LoadPropWithMaterial"/>. Deterministic (a coordinate hash, no RNG). Mirrors
    /// <c>TerrainMaterialPresets.Procedural</c> and returns raw-RGBA <see cref="GltfMaterialMaps"/> (no PNG encoder,
    /// no asset file). Upload with <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/>.</summary>
    public static class PropMaterialPresets
    {
        /// <summary>A mossy-stone albedo + a gentle derived tangent-space normal, each
        /// <paramref name="size"/> x <paramref name="size"/> RGBA8. Grey stone value-noise base with green moss
        /// mottling. The normal is the albedo-noise gradient (z dominant).</summary>
        public static GltfMaterialMaps Procedural(int size = 64, int seed = 1337)
        {
            if (size < 1) size = 1;
            var albedo = new byte[size * size * 4];
            var normal = new byte[size * size * 4];
            var stone = new Color(0.42f, 0.41f, 0.39f);
            var moss = new Color(0.24f, 0.38f, 0.16f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                float n = Noise(x, y, seed);                 // 0..1 stone value noise
                float m = Smooth(Noise(x, y, seed + 91));     // 0..1 moss mask
                float moss01 = m > 0.6f ? (m - 0.6f) / 0.4f : 0f;
                float v = 0.8f + 0.4f * n;
                albedo[i + 0] = ToByte((stone.R * v) * (1f - moss01) + moss.R * moss01);
                albedo[i + 1] = ToByte((stone.G * v) * (1f - moss01) + moss.G * moss01);
                albedo[i + 2] = ToByte((stone.B * v) * (1f - moss01) + moss.B * moss01);
                albedo[i + 3] = 255;

                float dx = Noise(x + 1, y, seed) - Noise(x - 1, y, seed);
                float dy = Noise(x, y + 1, seed) - Noise(x, y - 1, seed);
                normal[i + 0] = ToByte(0.5f - 0.4f * dx);
                normal[i + 1] = ToByte(0.5f - 0.4f * dy);
                normal[i + 2] = 255;
                normal[i + 3] = 255;
            }

            return new GltfMaterialMaps(
                new DecodedImage(albedo, size, size),
                new DecodedImage(normal, size, size),
                null);
        }

        static byte ToByte(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);
        static float Smooth(float f) => f * f * (3f - 2f * f);

        // Deterministic value noise from a coordinate hash (no RNG; tileable enough for a placeholder).
        static float Noise(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535f;
            }
        }
    }
}
