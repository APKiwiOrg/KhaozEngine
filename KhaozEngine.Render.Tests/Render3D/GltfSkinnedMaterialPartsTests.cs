using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class GltfSkinnedMaterialPartsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Memory_import_preserves_skin_order_and_material_order(bool slice)
    {
        byte[] glb = Fixture().Build();
        byte[] originalBytes = (byte[])glb.Clone();
        AttachmentGlb.ValidateSkinnedEmbedded(glb, "garment");
        string path = Path.Combine(Path.GetTempPath(), $"ke_skinned_{Guid.NewGuid():N}.glb");
        File.WriteAllBytes(path, glb);
        try
        {
            SkinnedGltfMesh pathMesh = GltfLoader.LoadSkinned(path);
            var pathParts = GltfLoader.LoadSkinnedPartsWithMaterials(path);
            var legacy = GltfLoader.LoadSkinnedWithMaterial(path);
            File.Delete(path);
            ReadOnlyMemory<byte> bytes = glb;
            if (slice)
            {
                var padded = new byte[glb.Length + 9];
                Array.Fill(padded, (byte)0xff);
                glb.CopyTo(padded, 4);
                bytes = padded.AsMemory(4, glb.Length);
            }

            SkinnedGltfMesh memoryMesh = GltfLoader.LoadSkinned(bytes, path);
            var skinnedParts = GltfLoader.LoadSkinnedPartsWithMaterials(bytes, path);
            Assert.Equal(originalBytes, bytes.ToArray());
            Assert.False(File.Exists(path));
            Assert.Equal(2, skinnedParts.Count); // first-use material order
            Skeleton skeleton = Assert.IsType<Skeleton>(memoryMesh.Skeleton);
            string[] skinBoneNames = skeleton.JointToNode.Select(i => skeleton.NodeNames[i]).ToArray();
            Assert.Equal(new[] { "knee", "hip" }, skinBoneNames);
            Assert.Equal(pathMesh.InverseBind, memoryMesh.InverseBind);
            Assert.Equal(pathMesh.RestPose, memoryMesh.RestPose);
            AssertMesh(pathMesh, memoryMesh);
            AssertMesh(pathMesh, legacy.Mesh);
            AssertMaps(pathParts[0].Maps, legacy.Maps);
            Assert.Same(skinnedParts[0].Mesh.Skeleton, skinnedParts[1].Mesh.Skeleton);
            for (int material = 0; material < skinnedParts.Count; material++)
            {
                AssertMesh(pathParts[material].Mesh, skinnedParts[material].Mesh);
                AssertMaps(pathParts[material].Maps, skinnedParts[material].Maps);
                Assert.Equal(memoryMesh.InverseBind, skinnedParts[material].Mesh.InverseBind);
                Assert.Equal(memoryMesh.RestPose, skinnedParts[material].Mesh.RestPose);
            }
            Assert.Equal(new[] { 0f, 4f }, skinnedParts[0].Mesh.Vertices.Chunk(3).Select(v => v[0].Position.X));
            Assert.Equal(2f, skinnedParts[1].Mesh.Vertices[0].Position.X);
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, skinnedParts[0].Mesh.Indices32);
            Assert.Equal(new uint[] { 0, 1, 2 }, skinnedParts[1].Mesh.Indices32);
            foreach (var part in skinnedParts)
                foreach (SkinnedVertex vertex in part.Mesh.Vertices)
                {
                    SkinnedVertex original = Assert.Single(memoryMesh.Vertices, v => v.Position == vertex.Position);
                    Assert.Equal(original, vertex);
                    Assert.Equal(new Vector4(0, 1, 0, 0), vertex.BoneIndices);
                    Assert.Equal(1f, vertex.BoneWeights.X + vertex.BoneWeights.Y);
                    Assert.Equal(part == skinnedParts[0] ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, -1), vertex.Tangent);
                }

            // The translated mesh node is ignored by skinning. Inverse binds carry the authored rig space.
            Assert.Equal(Vector3.Zero, skinnedParts[0].Mesh.Vertices[0].Position);
            Assert.Equal(Matrix4x4.CreateTranslation(-3, -6, -5), memoryMesh.InverseBind[0]);
            Assert.Equal(Matrix4x4.CreateTranslation(3, 6, 5), memoryMesh.RestPose[0]);
            var palette = memoryMesh.RestPose.Zip(memoryMesh.InverseBind, SkinningMath.Compose).ToArray();
            foreach (SkinnedVertex vertex in memoryMesh.Vertices)
                Assert.Equal(vertex.Position, SkinningMath.SkinVertex(vertex, palette).Position);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("VEC4", 5126)]
    [InlineData("VEC3", 5126)]
    [InlineData("VEC4", 5121)]
    [InlineData("VEC3", 5121)]
    [InlineData("VEC4", 5123)]
    [InlineData("VEC3", 5123)]
    [InlineData(null, 5126)]
    public void Same_gltf_material_has_identical_rigid_and_skinned_vertex_colour(string? colorType, int componentType)
    {
        byte[] glb = Fixture(colorType, componentType).Build();
        var rigidParts = GltfLoader.LoadPartsWithMaterials(glb, "rigid-colour-control");
        var skinnedParts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "skinned-colour");
        Assert.Equal(rigidParts.Count, skinnedParts.Count);
        for (int material = 0; material < skinnedParts.Count; material++)
        {
            Assert.Equal(rigidParts[material].Maps.AlphaCutoff, skinnedParts[material].Maps.AlphaCutoff);
            AssertMaps(rigidParts[material].Maps, skinnedParts[material].Maps);
            Assert.Equal(rigidParts[material].Mesh.Vertices.Length, skinnedParts[material].Mesh.Vertices.Length);
            for (int i = 0; i < skinnedParts[material].Mesh.Vertices.Length; i++)
            {
                ModelVertex rigidVertex = rigidParts[material].Mesh.Vertices[i];
                SkinnedVertex skinnedVertex = skinnedParts[material].Mesh.Vertices[i];
                Assert.Equal(rigidVertex.Color, skinnedVertex.Color);
                if (material == 0 && colorType == "VEC4" && componentType == 5126)
                    Assert.Equal(new Vector4(.2f, .15f, .2f, .4f), skinnedVertex.Color);
                Vector4 factor = material == 0 ? new(.4f, .6f, .8f, .5f) : new(.8f, .25f, .5f, 1);
                Vector4 color = colorType == null ? Vector4.One : new(.5f, .25f, .25f, colorType == "VEC3" ? 1 : .8f);
                if (componentType != 5126)
                {
                    float max = componentType == 5121 ? 255 : 65535;
                    color = new Vector4(MathF.Round(color.X * max), MathF.Round(color.Y * max),
                        MathF.Round(color.Z * max), MathF.Round(color.W * max)) / max;
                }
                Assert.Equal(factor * color, skinnedVertex.Color);
            }
        }
        Assert.Equal(.37f, skinnedParts[0].Maps.AlphaCutoff);
        Assert.Equal(new byte[] { 220, 20, 30, 255, 40, 50, 60, 128 }, skinnedParts[0].Maps.Albedo!.Value.Rgba);
        Assert.Equal(new byte[] { 128, 128, 255, 255, 100, 120, 240, 255 }, skinnedParts[0].Maps.Normal!.Value.Rgba);
        Assert.Equal(new byte[] { 0, 180, 40, 255, 10, 90, 30, 255 }, skinnedParts[0].Maps.Roughness!.Value.Rgba);
        Assert.True(skinnedParts[1].Maps.IsEmpty);
    }

    [Fact]
    public void Null_material_forms_its_own_untextured_group()
    {
        var fixture = Fixture(null);
        JsonObject primitive = fixture.Primitives[0]!.DeepClone().AsObject();
        primitive.Remove("material");
        fixture.Primitives.Add(primitive);
        byte[] glb = fixture.Build();
        var rigidParts = GltfLoader.LoadPartsWithMaterials(glb, "rigid-null-control");
        var skinnedParts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "null-material");
        Assert.Equal(3, skinnedParts.Count);
        Assert.Same(skinnedParts[0].Mesh.Skeleton, skinnedParts[2].Mesh.Skeleton);
        Assert.True(skinnedParts[2].Maps.IsEmpty);
        AssertMaps(rigidParts[2].Maps, skinnedParts[2].Maps);
        Assert.All(skinnedParts[2].Mesh.Vertices, v => Assert.Equal(new Vector4(.8f, .8f, .8f, 1), v.Color));
    }

    [Fact]
    public void Attachment_joint_cap_matches_renderer()
    {
        Assert.Equal(128, SkinningMath.MaxBonesPerDraw);
        byte[] atCap = new SkinnedAttachmentGlbFixture(SkinningMath.MaxBonesPerDraw).Build();
        AttachmentGlb.ValidateSkinnedEmbedded(atCap, "at-cap");
        Assert.Equal(128, GltfLoader.LoadSkinned(atCap, "at-cap").BoneCount);
        byte[] overCap = new SkinnedAttachmentGlbFixture(SkinningMath.MaxBonesPerDraw + 1).Build();
        Assert.Contains("1 to 128", Assert.Throws<InvalidDataException>(
            () => AttachmentGlb.ValidateSkinnedEmbedded(overCap, "over-cap")).Message);
        Assert.Contains("128-bone", Assert.Throws<InvalidOperationException>(
            () => GltfLoader.LoadSkinnedPartsWithMaterials(overCap, "over-cap")).Message);
    }

    [Theory]
    [InlineData("rigid-parts")]
    [InlineData("skinned-parts")]
    [InlineData("skinned")]
    public void Malformed_accessor_through_a_byte_overload_throws_invalid_data_naming_the_identity(string overload)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        int normals = fixture.Accessors.Count;
        fixture.Accessors.Add(new JsonObject
        {
            ["bufferView"] = fixture.AddView(new byte[3 * sizeof(float)]),
            ["componentType"] = 5126,
            ["count"] = 1,
            ["type"] = "VEC3",
        });
        fixture.SetElement(normals, 0, 0, 0, 1);
        fixture.Primitives[0]!["attributes"]!["NORMAL"] = normals;
        byte[] glb = fixture.Build();
        const string identity = "malformed-normal.glb";
        Action load = overload switch
        {
            "rigid-parts" => () => GltfLoader.LoadPartsWithMaterials(glb, identity),
            "skinned-parts" => () => GltfLoader.LoadSkinnedPartsWithMaterials(glb, identity),
            "skinned" => () => GltfLoader.LoadSkinned(glb, identity),
            _ => throw new ArgumentOutOfRangeException(nameof(overload), overload, null),
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(load);

        Assert.Contains(identity, error.Message);
        Assert.Equal("SharpGLTF.Validation", error.InnerException?.GetType().Namespace);
    }

    static SkinnedAttachmentGlbFixture Fixture(string? colorType = "VEC4", int colorComponentType = 5126)
    {
        var fixture = new SkinnedAttachmentGlbFixture(2);
        fixture.Nodes[0]!["name"] = "hip";
        fixture.Nodes[0]!["translation"] = new JsonArray(3, 4, 5);
        fixture.Nodes[1]!["name"] = "knee";
        fixture.Nodes[1]!["translation"] = new JsonArray(0, 2, 0);
        fixture.MeshNode["translation"] = new JsonArray(17, 19, 23);
        fixture.Skin["joints"] = new JsonArray(1, 0);
        fixture.SetInverseBind(0, Matrix4x4.CreateTranslation(-3, -6, -5));
        fixture.SetInverseBind(1, Matrix4x4.CreateTranslation(-3, -4, -5));
        fixture.Root["materials"] = JsonNode.Parse("""
            [{"pbrMetallicRoughness":{"baseColorFactor":[0.8,0.25,0.5,1]}},
             {"pbrMetallicRoughness":{"baseColorFactor":[0.4,0.6,0.8,0.5],"baseColorTexture":{"index":0},
              "metallicRoughnessTexture":{"index":2}},"normalTexture":{"index":1},"alphaMode":"MASK","alphaCutoff":0.37}]
            """);
        fixture.Root["images"] = new JsonArray();
        fixture.Root["textures"] = new JsonArray();
        AddImage(fixture, new byte[] { 220, 20, 30, 255, 40, 50, 60, 128 });
        AddImage(fixture, new byte[] { 128, 128, 255, 255, 100, 120, 240, 255 });
        AddImage(fixture, new byte[] { 0, 180, 40, 255, 10, 90, 30, 255 });
        for (int vertex = 0; vertex < 3; vertex++)
        {
            fixture.SetElement(1, vertex, 0, 1, 0, 0);
            fixture.SetElement(2, vertex, vertex == 0 ? .75f : .25f, vertex == 0 ? .25f : .75f, 0, 0);
        }
        JsonObject first = fixture.Primitives[0]!.AsObject();
        Attribute(fixture, first, "NORMAL", "VEC3", new[] { new[] { 0f, 0, 1 }, new[] { 0f, 0, 1 }, new[] { 0f, 0, 1 } });
        Attribute(fixture, first, "TEXCOORD_0", "VEC2", new[] { new[] { 0f, 0 }, new[] { 1f, 0 }, new[] { 0f, 1 } });
        if (colorType != null)
        {
            float[] color = colorType == "VEC3" ? new[] { .5f, .25f, .25f } : new[] { .5f, .25f, .25f, .8f };
            Attribute(fixture, first, "COLOR_0", colorType, new[] { color, color, color }, colorComponentType);
        }
        first["material"] = 1;
        for (int ordinal = 1; ordinal < 3; ordinal++)
        {
            JsonObject primitive = first.DeepClone().AsObject();
            primitive["material"] = ordinal == 1 ? 0 : 1;
            float x = ordinal * 2;
            int position = Attribute(fixture, primitive, "POSITION", "VEC3",
                new[] { new[] { x, 0, 0 }, new[] { x + 1, 0, 0 }, new[] { x, 1, 0 } });
            fixture.Accessors[position]!["min"] = new JsonArray(x, 0, 0);
            fixture.Accessors[position]!["max"] = new JsonArray(x + 1, 1, 0);
            if (ordinal == 1)
                Attribute(fixture, primitive, "TANGENT", "VEC4", Enumerable.Repeat(new[] { 0f, 1, 0, -1 }, 3).ToArray());
            fixture.Primitives.Add(primitive);
        }
        return fixture;
    }

    static int Attribute(SkinnedAttachmentGlbFixture fixture, JsonObject primitive, string semantic,
        string type, float[][] values, int componentType = 5126)
    {
        int size = componentType == 5121 ? 1 : componentType == 5123 ? 2 : 4;
        int accessor = fixture.Accessors.Count;
        var definition = new JsonObject
        {
            ["bufferView"] = fixture.AddView(new byte[values.Length * values[0].Length * size]),
            ["componentType"] = componentType,
            ["count"] = values.Length,
            ["type"] = type,
        };
        if (componentType != 5126) definition["normalized"] = true;
        fixture.Accessors.Add(definition);
        primitive["attributes"]![semantic] = accessor;
        for (int i = 0; i < values.Length; i++) fixture.SetElement(accessor, i, values[i]);
        return accessor;
    }

    static void AddImage(SkinnedAttachmentGlbFixture fixture, byte[] rgba)
    {
        JsonArray images = fixture.Root["images"]!.AsArray();
        images.Add(new JsonObject { ["bufferView"] = fixture.AddView(PngWriter.Encode(rgba, 2, 1)), ["mimeType"] = "image/png" });
        fixture.Root["textures"]!.AsArray().Add(new JsonObject { ["source"] = images.Count - 1 });
    }

    static void AssertMesh(SkinnedGltfMesh expected, SkinnedGltfMesh actual)
    {
        Assert.Equal(expected.Vertices, actual.Vertices);
        Assert.Equal(expected.Indices32, actual.Indices32);
        Assert.Equal(expected.IndexFormat, actual.IndexFormat);
        Assert.Equal(expected.InverseBind, actual.InverseBind);
        Assert.Equal(expected.RestPose, actual.RestPose);
        Assert.Equal(expected.Skeleton!.JointToNode, actual.Skeleton!.JointToNode);
        Assert.Equal(expected.Skeleton.NodeNames, actual.Skeleton.NodeNames);
    }

    static void AssertMaps(GltfMaterialMaps expected, GltfMaterialMaps actual)
    {
        Assert.Equal(expected.AlphaCutoff, actual.AlphaCutoff);
        AssertMap(expected.Albedo, actual.Albedo);
        AssertMap(expected.Normal, actual.Normal);
        AssertMap(expected.Roughness, actual.Roughness);
    }

    static void AssertMap(DecodedImage? expected, DecodedImage? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected is not DecodedImage image) return;
        Assert.NotNull(actual);
        Assert.Equal(image.Width, actual.Value.Width);
        Assert.Equal(image.Height, actual.Value.Height);
        Assert.Equal(image.Rgba, actual.Value.Rgba);
    }
}
