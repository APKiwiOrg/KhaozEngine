using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class ModelVertexTangentTests
    {
        [Fact]
        public void Vertex_is_64_bytes_with_tangent()
        {
            Assert.Equal(64u, ModelVertex.SizeInBytes);
            Assert.Equal(64, Marshal.SizeOf<ModelVertex>());
        }

        [Fact]
        public void Legacy_ctors_leave_tangent_zero()
        {
            var v4 = new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One, new Vector2(0.25f, 0.5f));
            var v3 = new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One);
            Assert.Equal(Vector4.Zero, v4.Tangent);
            Assert.Equal(Vector4.Zero, v3.Tangent);
            // Existing fields are unchanged.
            Assert.Equal(new Vector2(0.25f, 0.5f), v4.Uv);
        }

        [Fact]
        public void Five_arg_ctor_sets_tangent()
        {
            var t = new Vector4(1f, 0f, 0f, -1f);
            var v = new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One, Vector2.Zero, t);
            Assert.Equal(t, v.Tangent);
        }
    }
}
