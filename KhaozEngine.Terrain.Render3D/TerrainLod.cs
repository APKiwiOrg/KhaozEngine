using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Distance-to-LOD mapping for chunked terrain. PickLod chooses a tier from camera distance (near =
    /// dense, far = coarse); ResolutionFor gives that tier's grid resolution (segments per chunk edge). Which
    /// chunks exist and when they rebuild is the World streaming sub-project, not this one.</summary>
    public static class TerrainLod
    {
        public const int TierCount = 3;
        public const float NearMax = 80f;
        public const float MidMax = 200f;

        static readonly int[] Resolutions = { 64, 32, 16 };  // per chunk edge, by tier

        /// <summary>Tier 0 (dense) within NearMax, 1 within MidMax, else 2 (coarse). Monotone in distance.</summary>
        public static int PickLod(float distance)
        {
            if (distance < NearMax) return 0;
            if (distance < MidMax) return 1;
            return 2;
        }

        /// <summary>Grid resolution (segments per chunk edge) for a tier. Clamped to the valid tier range.</summary>
        public static int ResolutionFor(int lod) => Resolutions[Math.Clamp(lod, 0, TierCount - 1)];
    }
}
