using System;
using System.IO;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The byte overload of <see cref="GltfLoader.LoadFlattenedAlbedo(string)"/> parses the same mesh as the
    /// path overload, so a caller holding verified bytes never needs a file.</summary>
    public class GltfLoaderBytesTests
    {
        [Fact]
        public void LoadFlattenedAlbedo_FromBytesMatchesTheSameFileByPath()
        {
            string path = GltfMaterialAutoReadTests.WriteTexturedTriangleGlb();
            try
            {
                GltfMesh byPath = GltfLoader.LoadFlattenedAlbedo(path);
                GltfMesh byBytes = GltfLoader.LoadFlattenedAlbedo(File.ReadAllBytes(path), "textured-triangle");
                Assert.Equal(byPath.Vertices, byBytes.Vertices);
                Assert.Equal(byPath.Indices32, byBytes.Indices32);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
