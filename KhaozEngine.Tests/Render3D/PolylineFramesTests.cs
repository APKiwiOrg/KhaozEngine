using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class PolylineFramesTests
    {
        [Fact]
        public void StraightLineAlongZ_FramesAreTranslationsAtThePoints()
        {
            var pts = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 0, 2) };
            var frames = PolylineFrames.Build(pts, Axis.Z, Vector3.UnitY);
            Assert.Equal(3, frames.Length);
            for (int i = 0; i < pts.Length; i++)
            {
                Assert.True(Vector3.Distance(frames[i].Translation, pts[i]) < 1e-4f);
                // Straight chain along +Z: local +Z should map to world +Z.
                Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, frames[i]), Vector3.UnitZ) < 1e-4f,
                    $"frame {i} run axis should remain +Z for a straight chain along Z");
            }
        }

        [Fact]
        public void Frame_OrientsRunAxisAlongTheChainDirection()
        {
            // Chain turns from +Z to +X; the second frame's local +Z should point roughly +X in world.
            var pts = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(2, 0, 1) };
            var frames = PolylineFrames.Build(pts, Axis.Z, Vector3.UnitY);
            Vector3 localZ = Vector3.TransformNormal(Vector3.UnitZ, frames[1]);
            Assert.True(localZ.X > 0.5f, $"run axis should bend toward +X, got {localZ}");
        }
    }
}
