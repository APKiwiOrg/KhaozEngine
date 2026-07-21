using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>GPU-free ray vs analytic-terrain intersection: fixed-step march along the ray until the
    /// height difference (rayY minus the height sample) changes sign, then bisection refinement. Vector3 in/out
    /// so Terrain stays render-free (build the ray with a camera's ScreenToRay and pass its parts).
    /// Deterministic: same field/height function, ray, and parameters always give the same hit. Two overloads
    /// share one kernel: <see cref="Raycast(TerrainField, Vector3, Vector3, float, out Vector3, float, int)"/>
    /// against a concrete <see cref="TerrainField"/>, and <see cref="Raycast(Func{float, float, float}, Vector3,
    /// Vector3, float, out Vector3, float, int)"/> against a bare height function for a consumer that wants to
    /// raycast a height source that is not a <see cref="TerrainField"/> (a closed-form plane in a unit test, or a
    /// head-neutral sampler shared by two renderers) without hand-rolling its own march-then-bisect copy of this
    /// kernel.</summary>
    public static class TerrainRaycast
    {
        const int BisectIterations = 24;

        /// <summary>March from origin along direction (need not be normalized) up to maxDistance (in units
        /// of the direction's length) against field.SampleHeight. Returns true with the surface point when the
        /// ray crosses the terrain, false when it stays above for the whole distance. A ray starting below the
        /// surface returns true at the origin. step is the coarse march length in t units, bisected
        /// bisectIterations times on a crossing. The final march sample is clamped to exactly maxDistance, so a
        /// crossing inside the last partial step is still found. step must be positive (ArgumentOutOfRangeException
        /// otherwise). NaN fails that same check by explicit guard, since ThrowIfNegativeOrZero's "&lt;= 0"
        /// comparison is false for NaN and would otherwise pass it through to turn the whole march into a silent
        /// NaN-poisoned miss. maxDistance is guarded the same way for the same reason (a NaN maxDistance makes the
        /// very first loop condition false, again a silent miss instead of a clear reject). bisectIterations must
        /// not be negative (0 is allowed: the crossing is then reported at the coarse march resolution, with no
        /// refinement pass). This is a thin adapter over the <see cref="Func{Single, Single, Single}"/> overload,
        /// which holds the actual march/bisect kernel: field.SampleHeight is the height function.</summary>
        public static bool Raycast(TerrainField field, Vector3 origin, Vector3 direction, float maxDistance,
            out Vector3 hit, float step = 0.25f, int bisectIterations = BisectIterations)
        {
            ArgumentNullException.ThrowIfNull(field);
            return Raycast(field.SampleHeight, origin, direction, maxDistance, out hit, step, bisectIterations);
        }

        /// <summary>The march-then-bisect kernel, against a bare <paramref name="heightAt"/>(x, z) function
        /// instead of a concrete <see cref="TerrainField"/>. For a consumer that needs to raycast a height source
        /// that is not backed by a <see cref="TerrainField"/> (a closed-form plane in a headless unit test, or a
        /// sampler shared identically by two heads without either depending on Terrain's concrete type), so it does
        /// not have to hand-roll its own copy of this march/bisect logic. Same semantics as the
        /// <see cref="TerrainField"/> overload in every other respect: origin/direction/maxDistance/step/
        /// bisectIterations, the below-surface-origin short-circuit, the final-partial-step clamp, the NaN/
        /// non-positive step guard, the NaN maxDistance guard, and the non-negative bisectIterations guard all
        /// behave identically, since the TerrainField overload calls this one with field.SampleHeight as
        /// heightAt.</summary>
        public static bool Raycast(Func<float, float, float> heightAt, Vector3 origin, Vector3 direction,
            float maxDistance, out Vector3 hit, float step = 0.25f, int bisectIterations = BisectIterations)
        {
            ArgumentNullException.ThrowIfNull(heightAt);
            if (float.IsNaN(step)) throw new ArgumentOutOfRangeException(nameof(step), step, "step must not be NaN.");
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
            if (float.IsNaN(maxDistance)) throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "maxDistance must not be NaN.");
            ArgumentOutOfRangeException.ThrowIfNegative(bisectIterations);

            if (origin.Y - heightAt(origin.X, origin.Z) <= 0f)
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
                float diff = pos.Y - heightAt(pos.X, pos.Z);

                if (diff <= 0f)
                {
                    float lo = prevT, hi = t;
                    for (int i = 0; i < bisectIterations; i++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 midPos = origin + direction * mid;
                        float midDiff = midPos.Y - heightAt(midPos.X, midPos.Z);
                        if (midDiff <= 0f) hi = mid; else lo = mid;
                    }

                    Vector3 result = origin + direction * hi;
                    result.Y = heightAt(result.X, result.Z);
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
