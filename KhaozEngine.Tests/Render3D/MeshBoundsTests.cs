using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshBoundsTests
    {
        static ModelVertex V(float x, float y, float z) =>
            new(new Vector3(x, y, z), Vector3.UnitY, Vector4.One);

        [Fact]
        public void FromVertices_computes_aabb_center_and_radius()
        {
            var verts = new[] { V(-1, -2, -3), V(1, 2, 3), V(0, 0, 0) };
            var b = MeshBounds.FromVertices(verts);
            Assert.Equal(new Vector3(-1, -2, -3), b.Min);
            Assert.Equal(new Vector3(1, 2, 3), b.Max);
            Assert.Equal(Vector3.Zero, b.Center);
            // Half the diagonal of a (2,4,6) box.
            Assert.Equal(new Vector3(2, 4, 6).Length() * 0.5f, b.Radius, 4);
        }

        [Fact]
        public void Empty_span_is_degenerate_point()
        {
            var b = MeshBounds.FromVertices(System.Array.Empty<ModelVertex>());
            Assert.Equal(Vector3.Zero, b.Center);
            Assert.Equal(0f, b.Radius);
        }

        [Fact]
        public void WorldSphere_translates_center_and_scales_radius_by_max_axis()
        {
            var b = new MeshBounds(new Vector3(-1), new Vector3(1)); // unit cube, radius = sqrt(3)
            var world = Matrix4x4.CreateScale(2f, 3f, 1f) * Matrix4x4.CreateTranslation(10f, 0f, -5f);
            b.WorldSphere(world, out Vector3 c, out float r);
            Assert.Equal(new Vector3(10f, 0f, -5f), c);
            // Largest scale is 3 -> radius scaled by 3 (conservative under non-uniform scale).
            Assert.Equal(b.Radius * 3f, r, 4);
        }

        [Fact]
        public void WorldSphere_radius_unchanged_under_pure_rotation()
        {
            var b = new MeshBounds(new Vector3(-1), new Vector3(1));
            var world = Matrix4x4.CreateRotationY(0.9f) * Matrix4x4.CreateRotationX(0.3f);
            b.WorldSphere(world, out _, out float r);
            Assert.Equal(b.Radius, r, 4);
        }
    }
}
