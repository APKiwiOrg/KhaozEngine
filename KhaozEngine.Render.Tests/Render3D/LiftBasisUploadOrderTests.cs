using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class LiftBasisUploadOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flattened_lift_basis_maps_to_renderer_upload_order(bool useDefaultMaterial)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.SetIndexedMaterialParts([0, 1, 2], 2);
        fixture.Primitives[0]!["material"] = 1;
        fixture.Primitives[1]!["material"] = 0;
        if (useDefaultMaterial) fixture.Primitives[0]!.AsObject().Remove("material");
        fixture.Primitives.Add(fixture.Primitives[0]!.DeepClone());
        Vector3[][] positions =
        [
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY],
            [2 * Vector3.UnitZ, 2 * Vector3.UnitZ + Vector3.UnitX, 2 * Vector3.UnitZ + Vector3.UnitY],
        ];
        for (int primitive = 0; primitive < positions.Length; primitive++)
        {
            fixture.SetPositions(primitive, positions[primitive]);
            fixture.SetLiftAttributes(primitive, [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [0, .01f, .02f]);
        }
        JsonNode secondMeshPrimitive = fixture.Primitives[2]!;
        fixture.Primitives.RemoveAt(2);
        fixture.Root["meshes"]!.AsArray().Add(new JsonObject { ["primitives"] = new JsonArray(secondMeshPrimitive) });
        int secondMeshNode = fixture.Nodes.Count;
        fixture.Nodes.Add(new JsonObject { ["mesh"] = 1, ["skin"] = 0 });
        fixture.Root["scenes"]![0]!["nodes"]!.AsArray().Add(secondMeshNode);
        byte[] glb = fixture.Build();

        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "part.glb");
        LiftBasis basis = AttachmentGlb.LiftBasis(glb, "part.glb");

        Vector3[] flattened = positions.SelectMany(primitive => primitive).ToArray();
        Assert.Equal(2, parts.Count);
        Assert.Equal(flattened.Length, basis.Vertices.Count);
        Assert.Equal(flattened.Length, parts.Sum(part => part.Mesh.Vertices.Length));
        for (int index = 0; index < flattened.Length; index++)
        {
            LiftBasisVertex vertex = basis.Vertices[index];
            Assert.Equal(flattened[index], parts[vertex.MaterialPart].Mesh.Vertices[vertex.IndexInPart].Position);
        }
    }
}
