using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    public class PrimitiveSectorTests
    {
        [Fact]
        public void SectorSegments_scales_with_arc_length_and_has_a_floor()
        {
            // Tiny sweep -> floored; large radius + full sweep -> more segments.
            Assert.True(PrimitiveRenderer.SectorSegments(2f, 0.05f) >= 2);
            Assert.True(PrimitiveRenderer.SectorSegments(300f, MathF.Tau) >
                        PrimitiveRenderer.SectorSegments(20f, 0.2f));
        }

        [Fact]
        public void SectorSpoke_endpoints_lie_on_the_arc_at_the_right_angle()
        {
            // A sector centered on +X (dir angle 0), half-angle 90deg, range 10, sampled at the leading edge.
            Vector2 center = new(5f, 5f);
            float dirAngle = 0f, halfAngle = MathF.PI / 2f, range = 10f;
            // t=0 is the start edge (dirAngle - halfAngle), t=1 the end edge (dirAngle + halfAngle).
            Vector2 start = PrimitiveRenderer.SectorRimPoint(center, dirAngle, halfAngle, range, 0f);
            Vector2 end = PrimitiveRenderer.SectorRimPoint(center, dirAngle, halfAngle, range, 1f);
            Assert.Equal(range, (start - center).Length(), 3);
            Assert.Equal(range, (end - center).Length(), 3);
            // start edge points down-ish (angle -90deg): y < center.y; end edge up-ish: y > center.y.
            Assert.True(start.Y < center.Y);
            Assert.True(end.Y > center.Y);
        }
    }
}
