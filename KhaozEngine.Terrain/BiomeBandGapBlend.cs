using System;

namespace KhaozEngine.Terrain
{
    /// <summary>The two band shares used when no ordinary band blend covers a world Z coordinate.</summary>
    internal readonly struct BiomeBandGapBlend
    {
        BiomeBandGapBlend(int firstBand, float firstWeight, int secondBand, float secondWeight, int dominantBand)
        {
            FirstBand = firstBand;
            FirstWeight = firstWeight;
            SecondBand = secondBand;
            SecondWeight = secondWeight;
            DominantBand = dominantBand;
        }

        public int FirstBand { get; }
        public float FirstWeight { get; }
        public int SecondBand { get; }
        public float SecondWeight { get; }
        public int DominantBand { get; }

        /// <summary>Blends between the nearest effective support edges. A missing side uses the nearest band
        /// alone. Equal shares choose the earlier band in the source array.</summary>
        public static BiomeBandGapBlend At(ReadOnlySpan<BiomeBand> bands, float blend, float z)
        {
            int left = -1, right = -1;
            float leftEdge = float.NegativeInfinity, rightEdge = float.PositiveInfinity;

            for (int i = 0; i < bands.Length; i++)
            {
                ref readonly BiomeBand band = ref bands[i];
                float supportEnd = band.End + blend;
                if (supportEnd <= z && supportEnd > leftEdge)
                {
                    left = i;
                    leftEdge = supportEnd;
                }

                float supportStart = band.Start - blend;
                if (supportStart >= z && supportStart < rightEdge)
                {
                    right = i;
                    rightEdge = supportStart;
                }
            }

            if (left < 0) return Single(right >= 0 ? right : 0);
            if (right < 0) return Single(left);
            if (left == right || rightEdge <= leftEdge) return Single(Math.Min(left, right));

            float rightWeight = TerrainNoise.SmoothStep(leftEdge, rightEdge, z);
            float leftWeight = 1f - rightWeight;
            int dominant = leftWeight > rightWeight
                ? left
                : rightWeight > leftWeight ? right : Math.Min(left, right);
            return new BiomeBandGapBlend(left, leftWeight, right, rightWeight, dominant);
        }

        static BiomeBandGapBlend Single(int band) => new(band, 1f, -1, 0f, band);
    }
}
