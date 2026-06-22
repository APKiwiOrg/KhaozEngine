using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SurfaceShadingTests
    {
        [Fact]
        public void Decodes_flat_normal_texel_to_z_axis()
        {
            // 1x1 flat-normal default decodes to ~(0,0,1).
            byte[] texel = DefaultMaps.FlatNormalTexel();
            var rgb = new Vector3(texel[0] / 255f, texel[1] / 255f, texel[2] / 255f);
            var n = SurfaceShading.DecodeNormalSample(rgb);
            Assert.True(MathF.Abs(n.X) < 0.01f && MathF.Abs(n.Y) < 0.01f && MathF.Abs(n.Z - 1f) < 0.01f, $"{n}");
        }

        [Fact]
        public void Default_roughness_texel_is_zero_green()
        {
            byte[] texel = DefaultMaps.ZeroRoughnessTexel();
            Assert.Equal(0, texel[1]); // .g sampled by the shader
            Assert.Equal(255, texel[3]); // opaque
        }

        [Fact]
        public void Flat_normal_reproduces_geometric_normal()
        {
            var N = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.1f));
            var tangent = new Vector4(1f, 0f, 0f, 1f);
            var nTS = new Vector3(0f, 0f, 1f); // flat
            var got = SurfaceShading.PerturbNormal(N, tangent, nTS);
            Assert.True((got - N).Length() < 1e-4f, $"expected {N}, got {got}");
        }

        [Fact]
        public void Zero_tangent_falls_back_to_geometric_normal()
        {
            var N = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.1f));
            var got = SurfaceShading.PerturbNormal(N, Vector4.Zero, new Vector3(0.7f, 0f, 0.7f));
            Assert.True((got - N).Length() < 1e-4f);
        }

        [Fact]
        public void Tangent_space_tilt_pushes_normal_toward_tangent()
        {
            var N = Vector3.UnitZ;                 // geometric normal +Z
            var tangent = new Vector4(1f, 0f, 0f, 1f); // tangent +X
            var nTS = Vector3.Normalize(new Vector3(0.6f, 0f, 0.8f)); // tilt toward +x in tangent space
            var got = SurfaceShading.PerturbNormal(N, tangent, nTS);
            Assert.True(got.X > 0.1f, $"normal should tilt toward +X, got {got}");
            Assert.True(MathF.Abs(got.Length() - 1f) < 1e-4f);
        }

        [Fact]
        public void Roughness_zero_is_identity_for_spec_params()
        {
            var (s, e) = SurfaceShading.ApplyRoughness(0.8f, 48f, 0f);
            Assert.Equal(0.8f, s, 5);
            Assert.Equal(48f, e, 5);
        }

        [Fact]
        public void Higher_roughness_lowers_strength_and_exponent()
        {
            var (s0, e0) = SurfaceShading.ApplyRoughness(0.8f, 48f, 0.25f);
            var (s1, e1) = SurfaceShading.ApplyRoughness(0.8f, 48f, 0.75f);
            Assert.True(s1 < s0 && s0 < 0.8f);
            Assert.True(e1 < e0 && e0 < 48f);
            // Fully rough clamps the exponent to MinSpecExponent (>= 1) and kills strength.
            var (sFull, eFull) = SurfaceShading.ApplyRoughness(0.8f, 48f, 1f);
            Assert.Equal(0f, sFull, 5);
            Assert.Equal(SurfaceShading.MinSpecExponent, eFull, 5);
        }
    }
}
