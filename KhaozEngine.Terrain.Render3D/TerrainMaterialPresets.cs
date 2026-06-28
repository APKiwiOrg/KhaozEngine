using KhaozEngine.Primitives;

namespace KhaozEngine.Terrain
{
    /// <summary>Procedural placeholder terrain materials so the in-repo sample and tests run without shipping binary
    /// textures. Real games supply ambientCG-style CC0 tileable albedo/normal per layer. Deterministic (a coordinate
    /// hash, no RNG); proves the full splat pipeline (arrays, mips, triplanar, normal maps) end to end.</summary>
    public static class TerrainMaterialPresets
    {
        /// <summary>A five-layer material with tinted value-noise albedo + a gentle derived normal per layer
        /// (grass/dirt/rock/sand/snow), all <paramref name="size"/> x <paramref name="size"/> RGBA8.</summary>
        public static TerrainLayeredMaterial Procedural(int size = 128)
        {
            var grass = Layer(size, new Color(0.27f, 0.42f, 0.18f), roughness: 0.9f, tiles: 0.35f);
            var dirt  = Layer(size, new Color(0.34f, 0.30f, 0.24f), roughness: 0.9f, tiles: 0.30f);
            var rock  = Layer(size, new Color(0.44f, 0.42f, 0.40f), roughness: 0.7f, tiles: 0.20f);
            var sand  = Layer(size, new Color(0.76f, 0.70f, 0.50f), roughness: 0.85f, tiles: 0.40f);
            var snow  = Layer(size, new Color(0.93f, 0.94f, 0.96f), roughness: 0.4f, tiles: 0.25f);
            return new TerrainLayeredMaterial
            {
                Width = size, Height = size,
                Grass = grass, Dirt = dirt, Rock = rock, Sand = sand, Snow = snow,
            };
        }

        static TerrainMaterialLayer Layer(int size, Color baseColor, float roughness, float tiles)
        {
            var albedo = new byte[size * size * 4];
            var normal = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                float n = Noise(x, y);                 // 0..1 value noise
                float v = 0.85f + 0.30f * n;           // subtle albedo variation
                albedo[i + 0] = ToByte(baseColor.R * v);
                albedo[i + 1] = ToByte(baseColor.G * v);
                albedo[i + 2] = ToByte(baseColor.B * v);
                albedo[i + 3] = 255;
                // Gentle normal from the noise gradient (tangent space; b dominant).
                float dx = Noise(x + 1, y) - Noise(x - 1, y);
                float dy = Noise(x, y + 1) - Noise(x, y - 1);
                normal[i + 0] = ToByte(0.5f - 0.5f * dx);
                normal[i + 1] = ToByte(0.5f - 0.5f * dy);
                normal[i + 2] = 255;
                normal[i + 3] = 255;
            }
            return new TerrainMaterialLayer { AlbedoRgba = albedo, NormalRgba = normal, Tint = Color.White, TilesPerMetre = tiles, Roughness = roughness };
        }

        static byte ToByte(float f) => (byte)System.Math.Clamp((int)(f * 255f + 0.5f), 0, 255);

        // Deterministic value noise from a coordinate hash (no RNG; tileable enough for a placeholder).
        static float Noise(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }
}
