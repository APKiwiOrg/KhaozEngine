using System;
using System.IO;
using System.Text;
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

        [Fact]
        public void LoadFlattenedAlbedo_FromASliceOfALargerBuffer_ReadsOnlyTheSlice()
        {
            string path = GltfMaterialAutoReadTests.WriteTexturedTriangleGlb();
            try
            {
                byte[] glb = File.ReadAllBytes(path);
                var padded = new byte[glb.Length + 7];
                glb.CopyTo(padded, 3);
                GltfMesh byPath = GltfLoader.LoadFlattenedAlbedo(path);
                GltfMesh bySlice = GltfLoader.LoadFlattenedAlbedo(new ReadOnlyMemory<byte>(padded, 3, glb.Length), "sliced");
                Assert.Equal(byPath.Vertices, bySlice.Vertices);
                Assert.Equal(byPath.Indices32, bySlice.Indices32);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadFlattenedAlbedo_FromBytes_NamesABadIndexThroughTheValidationFallback()
        {
            byte[] glb = Glb(GltfIndexValidationTests.MalformedRigidGltfJson());
            GltfIndexValidationTests.AssertBadIndex(() => GltfLoader.LoadFlattenedAlbedo(glb, "bad-index-bytes"), "bad-index-bytes");
        }

        // A binary glTF holding only a JSON chunk, padded with spaces.
        static byte[] Glb(string json)
        {
            byte[] text = Encoding.UTF8.GetBytes(json);
            int padded = (text.Length + 3) & ~3;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x46546C67u);
                writer.Write(2u);
                writer.Write((uint)(12 + 8 + padded));
                writer.Write((uint)padded);
                writer.Write(0x4E4F534Au);
                writer.Write(text);
                for (int i = text.Length; i < padded; i++) writer.Write((byte)' ');
            }
            return stream.ToArray();
        }
    }
}
