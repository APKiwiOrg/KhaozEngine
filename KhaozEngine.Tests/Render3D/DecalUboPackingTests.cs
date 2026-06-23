using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class DecalUboPackingTests
    {
        [Fact]
        public void Pack_carries_shape_index_size_and_colors()
        {
            var d = new GroundDecal
            {
                Shape = DecalShape.Cone,
                Center = new Vector3(2f, 0.5f, -3f),
                Rotation = 1.25f,
                Size = new Vector4(7f, 0.4f, 0f, 0f),
                FillColor = new Color(0.2f, 0.3f, 0.4f, 0.5f),
                OutlineColor = new Color(1f, 0.9f, 0.1f, 0.8f),
                EdgeThickness = 0.15f,
                FillFraction = 0.6f,
                FlashAdd = 0.25f,
                Blend = DecalBlend.Additive,
                YTolerance = 0.5f,
                MaxStep = 1.5f,
            };
            Matrix4x4.Invert(Matrix4x4.Identity, out var inv);
            var u = GroundDecalRenderer.PackUbo(d, inv);

            Assert.Equal((float)(int)DecalShape.Cone, u.Params.W, 3); // shape index in Params.w
            Assert.Equal(d.Size, u.Size);
            Assert.Equal(d.Center.X, u.Center.X, 3);
            Assert.Equal(d.Rotation, u.Center.W, 3);                  // rotation packed in Center.w
            Assert.Equal(d.FillColor.R, u.Fill.X, 3);
            Assert.Equal(d.OutlineColor.A, u.Outline.W, 3);
            Assert.Equal(d.EdgeThickness, u.Params.X, 3);
            Assert.Equal(d.FillFraction, u.Params.Y, 3);
            Assert.Equal(d.FlashAdd, u.Params.Z, 3);
            Assert.Equal(d.Center.Y, u.Gate.X, 3);                    // groundY
            Assert.Equal(d.YTolerance, u.Gate.Y, 3);
            Assert.Equal(d.MaxStep, u.Gate.Z, 3);
        }
    }
}
