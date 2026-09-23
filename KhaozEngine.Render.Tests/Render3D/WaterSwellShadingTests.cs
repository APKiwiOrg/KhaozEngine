using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/381">#381</see>: the procedural
    /// swell's normal is evaluated per pixel at the still-water position instead of being interpolated from the grid.
    /// The GPU half, a rendered coarse ring measured for triangle facets, is <c>WaterSwellFacetGpuTests</c>. This half
    /// pins the source shape the fix depends on, and the numbers that justify it off the CPU mirror
    /// <see cref="GerstnerWaves"/>.
    /// </summary>
    public class WaterSwellShadingTests
    {
        [Fact]
        public void BothStagesCarryTheOneSwellBlockAndItsMirroredConstants()
        {
            string decay = GerstnerWaves.LambdaDecay.ToString("0.0##", CultureInfo.InvariantCulture);
            foreach ((string name, string source) in new[]
            {
                ("WaterVert", ShaderSources.WaterVert), ("WaterClipmapVert", ShaderSources.WaterClipmapVert),
                ("WaterFrag", ShaderSources.WaterFrag),
            })
            {
                Assert.True(source.Contains("void gerstnerEvaluate(vec2 aXz, float time, out vec3 offset, out vec3 normal, out float fold)",
                    StringComparison.Ordinal), $"{name} lost the shared swell evaluator");
                Assert.True(source.Contains($"const float KE_LAMBDA_DECAY = {decay};", StringComparison.Ordinal),
                    $"{name}'s swell ladder ratio drifted from GerstnerWaves.LambdaDecay");
                Assert.True(source.Contains($"const int   KE_MAX_SWELL = {GerstnerWaves.MaxComponents};", StringComparison.Ordinal),
                    $"{name}'s swell loop bound drifted from GerstnerWaves.MaxComponents");
            }
        }

        [Fact]
        public void TheFragmentEvaluatesTheSwellNormalAtTheStillWaterPosition()
        {
            // At vRefXz, the interpolated still-water position, and not at the displaced one: the trochoidal pinch
            // moves a point by metres, and a normal evaluated there belongs to a different part of the wave.
            Assert.Contains("gerstnerEvaluate(vRefXz, time, swellOffset, nSwell, swellFold);", ShaderSources.WaterFrag);
            // And no stage hands a swell normal down any more: an interpolated one is the defect.
            foreach (string source in new[] { ShaderSources.WaterVert, ShaderSources.WaterClipmapVert, ShaderSources.WaterFrag })
                Assert.DoesNotContain("vSwellNormal", source);
        }

        /// <summary>
        /// Why per pixel, in numbers. The Ruinborne lake's swell (42 m, four components, shortest 13.5 m) on the
        /// clipmap's own cell sizes: at the innermost ring's half-metre cells an interpolated vertex normal is already
        /// within a few hundredths of a degree of the evaluated one, so moving the evaluation into the fragment changes
        /// nothing the near field could show. At an outer ring's 16 m cells the interpolated normal's error is larger
        /// than the swell's own tilt, so what those rings drew was mostly interpolation, which is the facet.
        /// </summary>
        [Fact]
        public void AnInterpolatedSwellNormalIsOnlyRightWhereTheGridResolvesTheSwell()
        {
            var comps = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.35f, 42f, GerstnerWaves.DegreesToRadians(125f),
                GerstnerWaves.DegreesToRadians(55f), 0.6f, 0.6f, 0f, 4, comps);
            GerstnerWaves.Component[] stack = comps[..n];

            (float rms, float max, float tilt) near = Error(stack, cell: 0.5f);
            (float rms, float max, float tilt) far = Error(stack, cell: 16f);

            Assert.True(near.max < 0.1f,
                $"at 0.5 m cells the interpolated swell normal is up to {near.max:F3} degrees off the evaluated one");
            Assert.True(far.rms > far.tilt,
                $"at 16 m cells the interpolation error ({far.rms:F2} degrees RMS) no longer exceeds the swell's own " +
                $"mean tilt ({far.tilt:F2} degrees), so this no longer demonstrates the defect the fix is for");
        }

        /// <summary>RMS and worst angular error, in degrees, of the vertex-interpolated normal against the evaluated
        /// one over points inside the clipmap's triangles (<c>(i0, i2, i1)</c> and <c>(i1, i2, i3)</c>), plus the mean
        /// tilt of the evaluated normal for scale.</summary>
        static (float Rms, float Max, float Tilt) Error(GerstnerWaves.Component[] stack, float cell)
        {
            const float T = 3.7f;
            Vector3 At(float x, float z) => GerstnerWaves.Evaluate(x, z, T, 0.6f, stack).Normal;
            double sum = 0, tilt = 0, max = 0;
            int count = 0;
            for (int j = 0; j < 64; j++)
                for (int i = 0; i < 64; i++)
                {
                    // An irrational stride, so the probes land at every position inside the cells.
                    float x = 500f + i * 0.7310f * cell, z = 700f + j * 0.6172f * cell;
                    float gx = MathF.Floor(x / cell), gz = MathF.Floor(z / cell);
                    float fx = x / cell - gx, fz = z / cell - gz;
                    Vector3 n0 = At(gx * cell, gz * cell), n1 = At((gx + 1) * cell, gz * cell);
                    Vector3 n2 = At(gx * cell, (gz + 1) * cell), n3 = At((gx + 1) * cell, (gz + 1) * cell);
                    Vector3 lerp = fx + fz <= 1f
                        ? n0 * (1f - fx - fz) + n1 * fx + n2 * fz
                        : n3 * (fx + fz - 1f) + n1 * (1f - fz) + n2 * (1f - fx);
                    Vector3 exact = At(x, z);
                    double err = Math.Acos(Math.Clamp(Vector3.Dot(exact, Vector3.Normalize(lerp)), -1f, 1f)) * 180.0 / Math.PI;
                    sum += err * err;
                    max = Math.Max(max, err);
                    tilt += Math.Acos(Math.Clamp(exact.Y, -1f, 1f)) * 180.0 / Math.PI;
                    count++;
                }
            return ((float)Math.Sqrt(sum / count), (float)max, (float)(tilt / count));
        }
    }
}
