using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>GPU-free ray vs analytic-terrain intersection: fixed-step march along the ray until the
    /// height difference (rayY minus SampleHeight) changes sign, then bisection refinement. Vector3 in/out
    /// so Terrain stays render-free (build the ray with a camera's ScreenToRay and pass its parts).
    /// Deterministic: same field, ray, and parameters always give the same hit.</summary>
    public static class TerrainRaycast
    {
        const int BisectIterations = 24;

        /// <summary>March from origin along direction (need not be normalized) up to maxDistance (in units
        /// of the direction's length). Returns true with the surface point when the ray crosses the terrain,
        /// false when it stays above for the whole distance. A ray starting below the surface returns true
        /// at the origin. step is the coarse march length in t units, bisected 24 times on a crossing.
        /// The final march sample is clamped to exactly maxDistance, so a crossing inside the last partial
        /// step is still found. step must be positive (ArgumentOutOfRangeException otherwise). NaN fails that
        /// same check by explicit guard, since ThrowIfNegativeOrZero's "&lt;= 0" comparison is false for NaN and
        /// would otherwise pass it through to turn the whole march into a silent NaN-poisoned miss. maxDistance
        /// is guarded the same way for the same reason (a NaN maxDistance makes the very first loop condition
        /// false, again a silent miss instead of a clear reject).</summary>
        public static bool Raycast(TerrainField field, Vector3 origin, Vector3 direction, float maxDistance,
            out Vector3 hit, float step = 0.25f)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (float.IsNaN(step)) throw new ArgumentOutOfRangeException(nameof(step), step, "step must not be NaN.");
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
            if (float.IsNaN(maxDistance)) throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "maxDistance must not be NaN.");

            if (origin.Y - field.SampleHeight(origin.X, origin.Z) <= 0f)
            {
                hit = origin;
                return true;
            }

            float prevT = 0f;
            float t = step;

            while (prevT < maxDistance)
            {
                if (t > maxDistance) t = maxDistance;

                Vector3 pos = origin + direction * t;
                float diff = pos.Y - field.SampleHeight(pos.X, pos.Z);

                if (diff <= 0f)
                {
                    float lo = prevT, hi = t;
                    for (int i = 0; i < BisectIterations; i++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 midPos = origin + direction * mid;
                        float midDiff = midPos.Y - field.SampleHeight(midPos.X, midPos.Z);
                        if (midDiff <= 0f) hi = mid; else lo = mid;
                    }

                    Vector3 result = origin + direction * hi;
                    result.Y = field.SampleHeight(result.X, result.Z);
                    hit = result;
                    return true;
                }

                prevT = t;
                t += step;

                // Float accumulation can stall once t is huge relative to step (t + step == t).
                // Jump straight to the final sample so the march always terminates.
                if (t <= prevT) t = maxDistance;
            }

            hit = default;
            return false;
        }
    }
}
