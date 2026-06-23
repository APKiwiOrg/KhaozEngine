using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class GroundTelegraphMappingTests
    {
        [Fact]
        public void Circle_maps_radius_progress_and_style()
        {
            var d = GroundTelegraphs.BuildCircle(new Vector3(2f, 0.5f, -3f), 4f, 0.5f, TelegraphStyle.Generic);
            Assert.Equal(DecalShape.Circle, d.Shape);
            Assert.Equal(new Vector3(2f, 0.5f, -3f), d.Center);
            Assert.Equal(4f, d.Size.X, 3);                 // radius
            var r = TelegraphResolve.Resolve(0.5f, TelegraphStyle.Generic);
            Assert.Equal(r.FillFraction, d.FillFraction, 4);
            Assert.Equal(r.Blend == TelegraphBlend.Additive ? DecalBlend.Additive : DecalBlend.Alpha, d.Blend);
            Assert.Equal((Vector4)r.FillColor, (Vector4)d.FillColor);
        }

        [Fact]
        public void Cone_packs_range_halfangle_and_rotation_from_direction()
        {
            // dir = +Z (xz) -> rotation atan2(z=1, x=0) = pi/2.
            var d = GroundTelegraphs.BuildCone(Vector3.Zero, new Vector2(0f, 1f), 0.6f, 5f, 1f, TelegraphStyle.Fire);
            Assert.Equal(DecalShape.Cone, d.Shape);
            Assert.Equal(5f, d.Size.X, 3);                 // range
            Assert.Equal(0.6f, d.Size.Y, 3);               // halfAngle
            Assert.Equal(MathF.PI / 2f, d.Rotation, 3);
        }
    }
}
