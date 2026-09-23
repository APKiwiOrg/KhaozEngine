using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1100">#1100</see>: the
    /// procedural whitecap fold is evaluated per pixel at the still-water position, and eased toward a floor as the
    /// still-water pixel footprint grows. The GPU half is <c>WaterSwellFacetGpuTests</c> (no triangle-shaped foam on a
    /// coarse ring) and <c>WaterWhitecapCoverageGpuTests</c> (distant coverage stays near the per-vertex figure).
    /// </summary>
    public class WaterWhitecapFoldTests
    {
        const float SwellWavelength = 42f;
        const int SwellComponents = 4;

        [Fact]
        public void TheFragmentThresholdsTheAttenuatedPerPixelFold()
        {
            string frag = ShaderSources.WaterFrag;
            Assert.Contains("gerstnerEvaluate(vRefXz, time, swellOffset, nSwell, swellFold);", frag);
            Assert.Contains("crestFold = swellFold * foldAtten;", frag);
            Assert.Contains("smoothstep(threshold, threshold + KE_WHITECAP_SOFTNESS, crestFold)", frag);
            // No stage hands a fold down any more: an interpolated one is the defect.
            foreach (string source in new[] { ShaderSources.WaterVert, ShaderSources.WaterClipmapVert, frag })
                Assert.DoesNotContain("vFold", source);

            string floor = WaterMath.WhitecapFoldFloor.ToString("0.0##", CultureInfo.InvariantCulture);
            string samples = WaterMath.WhitecapFoldSamples.ToString("0.0", CultureInfo.InvariantCulture);
            Assert.Contains($"const float KE_FOLD_FLOOR = {floor};", frag);
            Assert.Contains($"const float KE_FOLD_SAMPLES = {samples};", frag);
        }

        [Fact]
        public void TheAttenuationIsOneUpCloseAndEasesMonotonicallyToTheFloor()
        {
            Assert.Equal(1f, WaterMath.WhitecapFoldAttenuation(SwellWavelength, SwellComponents, 0f), 6);
            // The shortest component (13.5 m) is fully resolved while the footprint stays under 13.5 / 80 m.
            Assert.Equal(1f, WaterMath.WhitecapFoldAttenuation(SwellWavelength, SwellComponents, 0.15f), 6);
            Assert.Equal(WaterMath.WhitecapFoldFloor,
                WaterMath.WhitecapFoldAttenuation(SwellWavelength, SwellComponents, 1e6f), 4);

            float previous = 1f;
            for (float footprint = 0.05f; footprint < 200f; footprint *= 1.25f)
            {
                float a = WaterMath.WhitecapFoldAttenuation(SwellWavelength, SwellComponents, footprint);
                Assert.True(a <= previous + 1e-6f, $"the attenuation rose again at a footprint of {footprint} m");
                Assert.True(a >= WaterMath.WhitecapFoldFloor - 1e-6f, $"the attenuation fell below the floor at {footprint} m");
                previous = a;
            }
        }

        [Fact]
        public void TheAttenuationIsTheMeanResolveOfTheSwellLadder()
        {
            const float footprint = 0.4f;
            float keep = 0f;
            for (int i = 0; i < SwellComponents; i++)
                keep += RippleSpectrum.Resolve(SwellWavelength * MathF.Pow(GerstnerWaves.LambdaDecay, i), footprint,
                    WaterMath.WhitecapFoldSamples);
            float expected = WaterMath.WhitecapFoldFloor + (1f - WaterMath.WhitecapFoldFloor) * keep / SwellComponents;
            float got = WaterMath.WhitecapFoldAttenuation(SwellWavelength, SwellComponents, footprint);
            Assert.Equal(expected, got, 6);
            Assert.InRange(got, WaterMath.WhitecapFoldFloor + 0.01f, 0.99f);
        }

        /// <summary>
        /// The footprint the attenuation reads is taken where the view ray meets the still-water plane, which depends
        /// on the ray alone. Any fragment along one ray maps to one point, however high the displaced surface stood,
        /// so the footprint has no reason to change where two displaced triangles meet (#1101).
        /// </summary>
        [Fact]
        public void TheStillWaterPointDependsOnTheViewRayAlone()
        {
            var eye = new Vector3(3f, 6f, -200f);
            const float surfaceY = 0.5f;
            var direction = Vector3.Normalize(new Vector3(0.2f, -0.04f, 1f));
            float tStill = (eye.Y - surfaceY) / -direction.Y;
            Vector3 onPlane = eye + direction * tStill;

            Vector2 reference = WaterMath.StillWaterPoint(eye, onPlane, surfaceY);
            Assert.Equal(onPlane.X, reference.X, 2);
            Assert.Equal(onPlane.Z, reference.Y, 2);
            foreach (float t in new[] { 0.6f, 0.9f, 1.1f, 1.4f })
            {
                // A crest or a trough hit on the same ray: nearer and higher, or further and lower.
                Vector2 p = WaterMath.StillWaterPoint(eye, eye + direction * (tStill * t), surfaceY);
                Assert.True(Vector2.Distance(reference, p) < 0.05f,
                    $"a fragment at {t} of the still-water distance mapped {Vector2.Distance(reference, p):F3} m away");
            }

            // No still plane to meet from at or below it: the fragment's own position.
            var fragment = new Vector3(10f, 0.2f, 40f);
            Assert.Equal(new Vector2(10f, 40f), WaterMath.StillWaterPoint(new Vector3(0f, surfaceY, 0f), fragment, surfaceY));
            // A crest above the eye is held at a thousand eye heights rather than flipping behind the camera.
            Vector2 capped = WaterMath.StillWaterPoint(eye, new Vector3(eye.X, eye.Y + 1f, eye.Z + 10f), surfaceY);
            Assert.True(capped.Y > eye.Z, "a fragment above the eye projected behind the camera");
        }

        /// <summary>
        /// The floor's strength, off the CPU mirror. Fully attenuated, the fold is 73% of itself, and at the shipped
        /// coverage that leaves about a quarter of the whitecap area the unattenuated fold has on the Ruinborne lake's
        /// swell, the same order as the share of the near field's coverage the per-vertex fold used to leave in the
        /// far field.
        /// </summary>
        [Fact]
        public void TheFloorLeavesAboutAQuarterOfTheWhitecapArea()
        {
            var comps = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.35f, SwellWavelength, GerstnerWaves.DegreesToRadians(125f),
                GerstnerWaves.DegreesToRadians(55f), 0.6f, 0.6f, 0f, SwellComponents, comps);
            ReadOnlySpan<GerstnerWaves.Component> stack = comps.AsSpan(0, n);

            int full = 0, floored = 0;
            for (int j = 0; j < 260; j++)
                for (int i = 0; i < 260; i++)
                {
                    float fold = GerstnerWaves.Evaluate(i * 0.77f, j * 0.69f, 3.7f, 0.6f, stack).Fold;
                    if (WaterMath.Whitecap(fold, 0.65f) >= 0.5f) full++;
                    if (WaterMath.Whitecap(fold * WaterMath.WhitecapFoldFloor, 0.65f) >= 0.5f) floored++;
                }

            Assert.True(full > 1000, $"only {full} of 67600 points whitecap unattenuated; the swell changed");
            Assert.InRange((float)floored / full, 0.1f, 0.45f);
        }
    }
}
