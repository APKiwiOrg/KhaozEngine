using System.IO;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class PropLoaderMaterialTests
    {
        static AssetEntry Entry(string file) =>
            new AssetEntry("p", file, heightMeters: 2f, source: "", license: "", textured: true);

        [Fact]
        public void LoadPropWithMaterial_TexturedGlb_ReturnsDecodedMaps()
        {
            string glb = GltfMaterialAutoReadTests.WriteTexturedTriangleGlb();
            try
            {
                (GltfMesh mesh, GltfMaterialMaps maps) = PropLoader.LoadPropWithMaterial(Entry(glb));
                Assert.False(maps.IsEmpty);
                Assert.True(maps.Albedo.HasValue);
                Assert.NotEmpty(mesh.Vertices);
            }
            finally { File.Delete(glb); }
        }

        [Fact]
        public void LoadPropWithMaterial_UntexturedGlb_DegradesToEmptyMaps_AndMeshMatchesLoadProp()
        {
            string glb = GltfMaterialAutoReadTests.WriteUntexturedTriangleGlb();
            try
            {
                (GltfMesh mesh, GltfMaterialMaps maps) = PropLoader.LoadPropWithMaterial(Entry(glb));
                Assert.True(maps.IsEmpty);

                GltfMesh plain = PropLoader.LoadProp(Entry(glb));
                Assert.Equal(plain.Vertices.Length, mesh.Vertices.Length);
                for (int i = 0; i < plain.Vertices.Length; i++)
                    Assert.Equal(plain.Vertices[i].Position, mesh.Vertices[i].Position);
            }
            finally { File.Delete(glb); }
        }
    }
}
