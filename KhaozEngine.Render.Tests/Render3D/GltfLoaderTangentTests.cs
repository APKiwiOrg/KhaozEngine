using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class GltfLoaderTangentTests
    {
        static string Asset => Path.Combine(AppContext.BaseDirectory, "assets", "testmodel.glb");

        [Fact]
        public void Loaded_tangents_are_finite_and_zero_or_unit()
        {
            Assert.True(File.Exists(Asset), $"test asset missing at {Asset}");
            var mesh = GltfLoader.Load(Asset);
            Assert.NotEmpty(mesh.Vertices);
            foreach (var v in mesh.Vertices)
            {
                var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                Assert.True(float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z)
                            && float.IsFinite(v.Tangent.W), "tangent has a non-finite component");
                float len = t.Length();
                Assert.True(len < 1e-4f || (len > 0.99f && len < 1.01f), $"tangent neither zero nor unit: {len}");
            }
        }
    }
}
