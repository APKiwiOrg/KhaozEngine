using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshPrimitivesTests
    {
        [Fact]
        public void Box_Has_24_Vertices_And_36_Indices()
        {
            var box = MeshPrimitives.Box();
            Assert.Equal(24, box.Vertices.Length);
            Assert.Equal(36, box.Indices.Length);
        }

        [Fact]
        public void Box_Indices_Are_All_In_Range()
        {
            var box = MeshPrimitives.Box();
            foreach (var idx in box.Indices)
                Assert.InRange(idx, 0, box.Vertices.Length - 1);
        }

        [Fact]
        public void Box_Has_8_Distinct_Corners_At_HalfSize()
        {
            const float size = 2f;
            float h = size * 0.5f;
            var box = MeshPrimitives.Box(size);

            var corners = box.Vertices.Select(v => v.Position).Distinct().ToList();
            Assert.Equal(8, corners.Count);
            foreach (var c in corners)
            {
                Assert.Equal(h, System.MathF.Abs(c.X), 4);
                Assert.Equal(h, System.MathF.Abs(c.Y), 4);
                Assert.Equal(h, System.MathF.Abs(c.Z), 4);
            }
        }

        [Fact]
        public void Tile_Base_Sits_At_Y0_And_Top_At_Thickness()
        {
            const float thickness = 0.25f;
            var tile = MeshPrimitives.Tile(1f, thickness);

            float minY = tile.Vertices.Min(v => v.Position.Y);
            float maxY = tile.Vertices.Max(v => v.Position.Y);
            Assert.Equal(0f, minY, 4);
            Assert.Equal(thickness, maxY, 4);
        }

        [Fact]
        public void Primitive_Vertex_Colors_Are_White()
        {
            foreach (var mesh in new[] { MeshPrimitives.Box(), MeshPrimitives.Tile() })
                foreach (var v in mesh.Vertices)
                    Assert.Equal(Vector4.One, v.Color);
        }
    }
}
