using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshOpsTangentTests
    {
        [Fact]
        public void WithTangents_BoxFaces_GetOrthonormalTangentWithHandedness()
        {
            GltfMesh box = MeshOps.WithTangents(MeshPrimitives.Box(2f));

            Assert.All(box.Vertices, v =>
            {
                var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                Assert.True(t.Length() > 0.99f && t.Length() < 1.01f, "tangent should be unit length");
                Assert.True(MathF.Abs(v.Tangent.W) == 1f, "handedness w must be +/-1");
                Assert.True(MathF.Abs(Vector3.Dot(t, v.Normal)) < 1e-3f, "tangent must be orthogonal to normal");
            });
        }

        [Fact]
        public void WithTangents_PreservesPositionsNormalsUvsAndIndices()
        {
            GltfMesh box = MeshPrimitives.Box(1f);
            GltfMesh tan = MeshOps.WithTangents(box);

            Assert.Equal(box.Vertices.Length, tan.Vertices.Length);
            Assert.Equal(box.Indices32, tan.Indices32);
            for (int i = 0; i < box.Vertices.Length; i++)
            {
                Assert.Equal(box.Vertices[i].Position, tan.Vertices[i].Position);
                Assert.Equal(box.Vertices[i].Normal, tan.Vertices[i].Normal);
                Assert.Equal(box.Vertices[i].Uv, tan.Vertices[i].Uv);
            }
        }
    }
}
