using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of <see cref="ModelRenderer.BuildLightArrays"/>: the pure packing that the dynamic
    /// point-light UBO upload relies on. Verifies lights are copied into the two fixed-size arrays, the active
    /// count is returned, the unused tail is zero-filled (so a previous frame can't leak), and an over-budget
    /// list is clamped to <see cref="ModelRenderer.MaxPointLights"/> (the host picks the N nearest).
    /// </summary>
    public class ModelRendererLightArraysTests
    {
        static ModelRenderer.PointLightData Light(float x, float r, float g, float b, float intensity)
            => new()
            {
                PosRadius = new Vector4(x, 0f, 0f, 2f),
                ColorIntensity = new Vector4(r, g, b, intensity),
            };

        [Fact]
        public void CopiesLights_ReturnsCount_ZeroFillsTail()
        {
            var lights = new[] { Light(1f, 1f, 0f, 0f, 3f), Light(2f, 0f, 1f, 0f, 4f) };
            var pos = new Vector4[ModelRenderer.MaxPointLights];
            var col = new Vector4[ModelRenderer.MaxPointLights];

            int count = ModelRenderer.BuildLightArrays(lights, pos, col);

            Assert.Equal(2, count);
            Assert.Equal(1f, pos[0].X, 4);
            Assert.Equal(2f, pos[0].W, 4);                 // radius preserved
            Assert.Equal(new Vector4(1f, 0f, 0f, 3f), col[0]);
            Assert.Equal(2f, pos[1].X, 4);
            Assert.Equal(new Vector4(0f, 1f, 0f, 4f), col[1]);
            // Tail beyond the active count is zeroed.
            for (int i = count; i < ModelRenderer.MaxPointLights; i++)
            {
                Assert.Equal(Vector4.Zero, pos[i]);
                Assert.Equal(Vector4.Zero, col[i]);
            }
        }

        [Fact]
        public void EmptyList_ReturnsZero_ArraysAllZero()
        {
            var pos = new Vector4[ModelRenderer.MaxPointLights];
            var col = new Vector4[ModelRenderer.MaxPointLights];
            // Pre-dirty the arrays to prove they get cleared.
            for (int i = 0; i < ModelRenderer.MaxPointLights; i++) { pos[i] = Vector4.One; col[i] = Vector4.One; }

            int count = ModelRenderer.BuildLightArrays(ReadOnlySpan<ModelRenderer.PointLightData>.Empty, pos, col);

            Assert.Equal(0, count);
            for (int i = 0; i < ModelRenderer.MaxPointLights; i++)
            {
                Assert.Equal(Vector4.Zero, pos[i]);
                Assert.Equal(Vector4.Zero, col[i]);
            }
        }

        [Fact]
        public void OverBudget_ClampsToMax_KeepsFirstN()
        {
            int extra = 5;
            var lights = new ModelRenderer.PointLightData[ModelRenderer.MaxPointLights + extra];
            for (int i = 0; i < lights.Length; i++) lights[i] = Light(i, 1f, 1f, 1f, 1f);
            var pos = new Vector4[ModelRenderer.MaxPointLights];
            var col = new Vector4[ModelRenderer.MaxPointLights];

            int count = ModelRenderer.BuildLightArrays(lights, pos, col);

            Assert.Equal(ModelRenderer.MaxPointLights, count);
            // The first MaxPointLights lights are kept in order; the over-budget tail is dropped.
            for (int i = 0; i < ModelRenderer.MaxPointLights; i++)
                Assert.Equal((float)i, pos[i].X, 4);
        }
    }
}
