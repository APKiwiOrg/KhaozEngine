using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageImportTests
{
    [Fact]
    public void Material_grouping_retains_exact_source_identity()
    {
        byte[] glb = HostFixture().Build();
        var map = Map(glb);
        var legacy = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host");
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", map);
        Assert.Equal(2, parts.Count);
        for (int slice = 0; slice < 2; slice++)
        {
            var mesh = parts[slice].Mesh;
            var coverage = Assert.IsType<HostCoveragePart>(mesh.Coverage);
            Assert.Equal(slice, coverage.MaterialSlice);
            Assert.Equal(slice, coverage.Source.Materials[slice].SourceMaterial);
            Assert.Equal(slice == 0 ? new CoverageSourceTriangle(0, 0, 0, -1, 0, 4, 0, 1)
                : new CoverageSourceTriangle(0, 1, 0, -1, 1, 9, 7, 8), coverage.SourceTriangles[0]);
            Assert.Equal(new[] { 0, 1 }, coverage.SourceTriangles.Select(t => t.Triangle));
            Assert.All(coverage.SourceTriangles, t => Assert.Equal(slice, t.Primitive));
            Assert.Equal(MemoryMarshal.AsBytes(legacy[slice].Mesh.Vertices.AsSpan()).ToArray(),
                MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()).ToArray());
            Assert.Equal(legacy[slice].Mesh.Indices32, mesh.Indices32);
            Assert.All(mesh.Vertices, v => Assert.Equal(Vector2.Zero, v.Uv));
            Assert.True(coverage.Matches(mesh));
            Matrix4x4 inverseBind = mesh.InverseBind[0];
            mesh.InverseBind[0] = Matrix4x4.CreateTranslation(1, 2, 3);
            Assert.False(coverage.Matches(mesh));
            mesh.InverseBind[0] = inverseBind;
            Assert.True(coverage.Matches(mesh));
        }
        Assert.Same(parts[0].Mesh.Coverage!.Source, parts[1].Mesh.Coverage!.Source);
        Assert.Equal(new uint[] { 4, 0, 1, 4, 1, 2 }, parts[0].Mesh.Indices32);
        byte[] before = HostCoverageMapJson.Write(parts[0].Mesh.Coverage!.Map);
        glb.AsSpan().Fill(0);
        Assert.Equal(before, HostCoverageMapJson.Write(parts[0].Mesh.Coverage!.Map));
        parts[0].Mesh.Vertices[0].Position = Vector3.One;
        Assert.False(parts[0].Mesh.Coverage!.Matches(parts[0].Mesh));
        Assert.Equal(new Vector3(-1, -1, 0), parts[0].Mesh.Coverage!.Source.Positions[0]);
    }

    [Fact]
    public void Changed_source_or_metadata_refuses()
    {
        var fixture = HostFixture();
        byte[] glb = fixture.Build();
        var map = Map(glb);
        fixture.MeshNode["translation"] = new JsonArray(0, 0, 1);
        Assert.Contains("host", Assert.Throws<InvalidDataException>(() =>
            GltfLoader.LoadSkinnedPartsWithMaterials(fixture.Build(), "host", map)).Message);
        Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", Map(glb, boundary: .2f)));
        Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", Map(glb, core: true)));
        Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", Map(glb, invalidRange: true)));
        fixture = HostFixture();
        fixture.SetElement(fixture.Primitives[0]!["attributes"]!["JOINTS_0"]!.GetValue<int>(), 0, 0, 0, 0, 0);
        fixture.SetElement(fixture.Primitives[0]!["attributes"]!["WEIGHTS_0"]!.GetValue<int>(), 0, 1, 0, 0, 0);
        glb = fixture.Build();
        Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", Map(glb)));
    }

    [Fact]
    public void Support_matches_all_displayed_node_occurrences()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.MeshNode["scale"] = new JsonArray(2, 3, 4);
        fixture.MeshNode["translation"] = new JsonArray(5, 7, 11);
        fixture.Nodes.Add(new JsonObject { ["mesh"] = 0, ["translation"] = new JsonArray(9, 7, 11) });
        fixture.Root["scenes"]![0]!["nodes"]!.AsArray().Add(fixture.Nodes.Count - 1);
        byte[] glb = fixture.Build();
        var source = GltfLoader.LoadCoverageSource(glb, "equipment");
        Assert.Equal(new[] { new Vector3(5, 7, 11), new Vector3(7, 7, 11), new Vector3(5, 10, 11),
            new Vector3(9, 7, 11), new Vector3(10, 7, 11), new Vector3(9, 8, 11) }, source.Positions.ToArray());
        Assert.Equal(2, source.Triangles.Count);
        Assert.Equal(new[] { 4, 5 }, source.Triangles.Select(t => t.Node));
        var displayed = GltfLoader.LoadPartsWithMaterials(glb, "equipment")[0].Mesh;
        Assert.Equal(displayed.Vertices.Select(v => v.Position), source.Positions.ToArray());
        Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(source,
            new[] { new CoverageTriangleRange(0, 0, 0, 1) }, Matrix4x4.Identity));
    }

    [Fact]
    public void Rigid_source_uses_displayed_welded_positions()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        JsonObject duplicate = fixture.Primitives[0]!.DeepClone().AsObject();
        Attribute(fixture, duplicate, "POSITION", "VEC3", new[] { new[] { .00001f, 0, 0 }, new[] { 1f, 0, 0 }, new[] { 0f, 1, 0 } });
        fixture.Primitives.Add(duplicate);
        byte[] glb = fixture.Build();
        var displayed = GltfLoader.LoadPartsWithMaterials(glb, "weld")[0].Mesh;
        var source = GltfLoader.LoadCoverageSource(glb, "weld");
        Assert.Equal(displayed.Vertices[displayed.Indices32[3]].Position, source.Positions[3]);
        Assert.Equal((int)displayed.Indices32[3], source.Vertices[3].SliceVertex);
    }

    [Fact]
    public void Contiguous_displayed_occurrences_form_one_sheet()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.Nodes.Add(new JsonObject
        {
            ["mesh"] = 0,
            ["matrix"] = new JsonArray(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1)
        });
        fixture.Root["scenes"]![0]!["nodes"]!.AsArray().Add(fixture.Nodes.Count - 1);
        var source = GltfLoader.LoadCoverageSource(fixture.Build(), "occurrences");
        var support = CoverageSupportSurface.Compile(source, new[] { new CoverageTriangleRange(0, 0, 0, 1) }, Matrix4x4.Identity);
        Assert.Equal(2, support.InwardTriangles.Count);
        Assert.True(support.TryGetHeight(new(.75f, .75f), out _));
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("overlap")]
    [InlineData("cutout")]
    public void Unsupported_displayed_occurrences_refuse(string kind)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        if (kind == "mirror") fixture.MeshNode["scale"] = new JsonArray(-1, 1, 1);
        if (kind == "overlap")
        {
            fixture.Nodes.Add(new JsonObject { ["mesh"] = 0 });
            fixture.Root["scenes"]![0]!["nodes"]!.AsArray().Add(fixture.Nodes.Count - 1);
        }
        if (kind == "cutout")
        {
            fixture.Root["materials"] = new JsonArray(new JsonObject { ["alphaMode"] = "MASK" });
            fixture.Primitives[0]!["material"] = 0;
        }
        var source = GltfLoader.LoadCoverageSource(fixture.Build(), "unsupported-occurrence");
        Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(source, new[] { new CoverageTriangleRange(0, 0, 0, 1) }, Matrix4x4.Identity));
    }

    [Fact]
    public void Compatible_skin_is_required_without_baking_mesh_nodes()
    {
        var fixture = HostFixture();
        fixture.MeshNode["translation"] = new JsonArray(13, 17, 19);
        byte[] glb = fixture.Build();
        Assert.Equal(new Vector3(-1, -1, 0), GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", Map(glb))[0].Mesh.Vertices[0].Position);
        fixture.Root["skins"]!.AsArray().Add(fixture.Skin.DeepClone());
        fixture.Nodes.Add(new JsonObject { ["mesh"] = 0, ["skin"] = 1 });
        fixture.Root["scenes"]![0]!["nodes"]!.AsArray().Add(fixture.Nodes.Count - 1);
        glb = fixture.Build();
        Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinnedPartsWithMaterials(glb, "host", Map(glb)));
    }

    [Fact]
    public void Affected_vertex_with_disconnected_one_ring_refuses()
    {
        Vector3[] positions = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(-1, 0, 0), new(0, -1, 0) };
        var vertices = positions.Select(p => new SkinnedVertex { Position = p, BoneWeights = Vector4.UnitX }).ToArray();
        var triangles = new[] { new CoverageSourceTriangle(0, 0, 0, -1, 0, 0, 1, 2), new CoverageSourceTriangle(0, 0, 1, -1, 0, 0, 3, 4) };
        string hash = new('0', 64);
        var source = new CoverageSourceTopology("bowtie", hash, positions,
            Enumerable.Range(0, 5).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)), triangles, vertices);
        var map = new HostCoverageMap(hash, Enumerable.Range(0, 5).Select(v => new CoverageTopologyEntry(0, 0, v, v)),
            new[] { new HostCoverageRegion("patch", new[] { new HostCoverageRegionTriangles(new(0, 0, 0, 2), CoverageTriangleRole.Collar) },
                Enumerable.Range(0, 5).Select(v => new CoverageSurfaceStrength(v, 0))) },
            Array.Empty<HostCoverageRefinement>(), new("synthetic", "fixture"));
        var mesh = new SkinnedGltfMesh(vertices, new uint[] { 0, 1, 2, 0, 3, 4 }, new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity });
        Assert.Throws<InvalidDataException>(() => HostCoveragePart.Create(map, source, 0, mesh));
    }

    [Fact]
    public void Rigid_source_preserves_vertex_ordinals_above_ushort()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        var points = Enumerable.Repeat(new[] { 0f, 0, 0 }, 65_537).ToArray();
        points[1] = new[] { 1f, 0, 0 };
        points[65_536] = new[] { 0f, 1, 0 };
        Attribute(fixture, fixture.Primitives[0]!.AsObject(), "POSITION", "VEC3", points);
        byte[] indices = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(indices, 65_536);
        BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(8), 1);
        fixture.Primitives[0]!["indices"] = fixture.Accessors.Count;
        fixture.Accessors.Add(new JsonObject { ["bufferView"] = fixture.AddView(indices), ["componentType"] = 5125, ["count"] = 3, ["type"] = "SCALAR" });
        fixture.Primitives[0]!["attributes"]!.AsObject().Remove("JOINTS_0");
        fixture.Primitives[0]!["attributes"]!.AsObject().Remove("WEIGHTS_0");
        fixture.MeshNode.Remove("skin");
        var source = GltfLoader.LoadCoverageSource(fixture.Build(), "wide-source");
        Assert.Equal(65_536, source.Vertices[0].Vertex);
        Assert.Equal(new Vector3(0, 1, 0), source.Positions[0]);
        Assert.Equal(new Vector3(0, 0, 0), source.Positions[1]);
        Assert.Equal(new Vector3(1, 0, 0), source.Positions[2]);
    }

    [Fact]
    public void Programmatic_core_and_collar_own_their_source_arrays()
    {
        var points = Enumerable.Range(0, 16).Select(v => new Vector3(v % 4, v / 4, 0)).ToArray();
        var vertices = points.Select(p => new SkinnedVertex { Position = p, BoneWeights = Vector4.UnitX }).ToArray();
        var indices = Enumerable.Range(0, 9).SelectMany(cell =>
        {
            int a = cell / 3 * 4 + cell % 3;
            return new uint[] { (uint)a, (uint)(a + 1), (uint)(a + 5), (uint)a, (uint)(a + 5), (uint)(a + 4) };
        }).ToArray();
        var triangles = Enumerable.Range(0, 18).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0,
            (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])).ToArray();
        string hash = new('0', 64);
        var source = new CoverageSourceTopology("grid", hash, points,
            Enumerable.Range(0, 16).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)), triangles, vertices);
        var topology = Enumerable.Range(0, 16).Select(v => new CoverageTopologyEntry(0, 0, v, v)).ToArray();
        var map = new HostCoverageMap(hash, topology, new[] { new HostCoverageRegion("patch", new[] {
            new HostCoverageRegionTriangles(new(0, 0, 0, 8), CoverageTriangleRole.Collar),
            new HostCoverageRegionTriangles(new(0, 0, 8, 2), CoverageTriangleRole.Core),
            new HostCoverageRegionTriangles(new(0, 0, 10, 8), CoverageTriangleRole.Collar) },
            Enumerable.Range(0, 16).Select(v => new CoverageSurfaceStrength(v, v % 4 is 1 or 2 && v / 4 is 1 or 2 ? 1 : 0))) },
            Array.Empty<HostCoverageRefinement>(), new("synthetic", "fixture"));
        byte[] before = HostCoverageMapJson.Write(map);
        points[0] = new(99);
        topology[0] = new(9, 9, 9, 9);
        triangles[0] = default;
        var mesh = new SkinnedGltfMesh(vertices, indices, new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity });
        var coverage = HostCoveragePart.Create(map, source, 0, mesh);
        Assert.True(coverage.Matches(mesh));
        Assert.Equal(Vector3.Zero, source.Positions[0]);
        Assert.Equal(before, HostCoverageMapJson.Write(coverage.Map));
        Assert.Equal(18, coverage.SourceTriangles.Count);
    }

    internal static SkinnedAttachmentGlbFixture HostFixture()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.Root["materials"] = new JsonArray(new JsonObject(), new JsonObject());
        float[][] points = { new[] { -1f, -1, 0 }, new[] { 1f, -1, 0 }, new[] { 1f, 1, 0 }, new[] { -1f, 1, 0 }, new[] { 0f, 0, 0 } };
        for (int primitive = 0; primitive < 2; primitive++)
        {
            JsonObject prim = primitive == 0 ? fixture.Primitives[0]!.AsObject() : new JsonObject { ["attributes"] = new JsonObject() };
            prim["material"] = primitive;
            Attribute(fixture, prim, "POSITION", "VEC3", points);
            Attribute(fixture, prim, "NORMAL", "VEC3", Enumerable.Repeat(new[] { 0f, primitive == 0 ? 0 : .6f, primitive == 0 ? 1 : .8f }, 5).ToArray());
            Attribute(fixture, prim, "JOINTS_0", "VEC4", Enumerable.Repeat(primitive == 0 ? new[] { 0f, 1, 2, 3 } : new[] { 3f, 2, 1, 0 }, 5).ToArray());
            Attribute(fixture, prim, "WEIGHTS_0", "VEC4", Enumerable.Repeat(primitive == 0 ? new[] { .1f, .2f, .3f, .4f } : new[] { .4f, .3f, .2f, .1f }, 5).ToArray());
            uint[] indices = primitive == 0 ? new uint[] { 4, 0, 1, 4, 1, 2 } : new uint[] { 4, 2, 3, 4, 3, 0 };
            byte[] bytes = new byte[24];
            for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), indices[i]);
            int accessor = fixture.Accessors.Count;
            fixture.Accessors.Add(new JsonObject { ["bufferView"] = fixture.AddView(bytes), ["componentType"] = 5125, ["count"] = 6, ["type"] = "SCALAR" });
            prim["indices"] = accessor;
            if (primitive > 0) fixture.Primitives.Add(prim);
        }
        return fixture;
    }

    internal static HostCoverageMap Map(byte[] glb, float boundary = 0, bool core = false, bool invalidRange = false)
    {
        var topology = Enumerable.Range(0, 2).SelectMany(p => Enumerable.Range(0, 5).Select(v => new CoverageTopologyEntry(0, p, v, v))).ToArray();
        var region = new HostCoverageRegion("patch", Enumerable.Range(0, 2).Select(p => new HostCoverageRegionTriangles(
            new CoverageTriangleRange(0, p, invalidRange ? 1 : 0, 2), core ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar)),
            Enumerable.Range(0, 5).Select(v => new CoverageSurfaceStrength(v, v == 4 ? 1 : boundary)));
        return new HostCoverageMap(Convert.ToHexStringLower(SHA256.HashData(glb)), topology, new[] { region },
            new[] { new HostCoverageRefinement(new CoverageTriangleRange(0, 0, 0, 2), 1) }, new("synthetic", "fixture"));
    }

    static void Attribute(SkinnedAttachmentGlbFixture fixture, JsonObject prim, string semantic, string type, float[][] values)
    {
        int accessor = fixture.Accessors.Count;
        int componentType = semantic == "JOINTS_0" ? 5123 : 5126;
        int componentSize = componentType == 5123 ? 2 : 4;
        var definition = new JsonObject
        {
            ["bufferView"] = fixture.AddView(new byte[values.Length * values[0].Length * componentSize]),
            ["componentType"] = componentType,
            ["count"] = values.Length,
            ["type"] = type
        };
        if (semantic == "POSITION") { definition["min"] = new JsonArray(-1, -1, 0); definition["max"] = new JsonArray(1, 1, 0); }
        fixture.Accessors.Add(definition);
        prim["attributes"]![semantic] = accessor;
        for (int i = 0; i < values.Length; i++) fixture.SetElement(accessor, i, values[i]);
    }
}
