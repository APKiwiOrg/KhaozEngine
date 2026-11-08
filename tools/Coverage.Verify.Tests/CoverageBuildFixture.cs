using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Tests.Attachments;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

internal static class CoverageBuildFixture
{
    internal static CoverageBuildInput Input(bool repeatedKey = false, bool duplicateCandidate = false)
    {
        var panel = new ExactPanelFixture(seams: false);
        var first = panel.Command with { MaxWorldRecession = .5f };
        // The second frame doubles world displacement per scalar. Its smaller own row clears,
        // the first row exceeds its world budget, and its own row is too small in the first frame.
        var shift = Matrix4x4.CreateScale(1, 1, 2);
        var second = first with { EquipmentWorld = shift, CoverageWorld = shift };
        CoverageSample[] samples = [
            new("a", "clip", "frame-a", null, new(1), Matrix4x4.Identity, panel.Palette, [first]),
            new("b", "clip", "frame-b", null, new(repeatedKey ? 1ul : 2ul), Matrix4x4.Identity, panel.Palette, [second])];
        if (duplicateCandidate)
            samples = [.. samples, new("c", "clip", "frame-a", null, new(1), Matrix4x4.Identity, panel.Palette, [first])];
        return new(panel.Topology, [Matrix4x4.Identity, Matrix4x4.Identity],
            [new("clip", CoverageDomainKind.AuthoredClip, ["frame-a", "frame-b"], [])], samples,
            duplicateCandidate ? ["a", "b", "c"] : ["a", "b"],
            new(CoverageBuildFixture.Hash, CoverageBuildFixture.Hash, CoverageBuildFixture.Hash,
                CoverageBuildFixture.Hash, CoverageBuildFixture.Hash, CoverageBuildFixture.Hash), Hash);
    }

    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}

internal sealed class CoverageBuildFiles : IDisposable
{
    internal readonly string Folder;
    internal readonly string ManifestPath;
    internal readonly JsonObject Manifest;
    internal readonly JsonObject Configuration;
    internal string TablePath => Path.Combine(Folder, "table.bin");
    internal string EvidencePath => Path.Combine(Folder, "evidence.json");
    internal CoverageBuildFiles()
    {
        Folder = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "khaozengine-equipment-fit-task6", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ManifestPath = Path.Combine(Folder, "manifest.json");
        var panel = new ExactPanelFixture(seams: false);
        byte[] host = Mesh(panel.Data.Source.Positions.ToArray(), panel.Data.Source.Triangles.SelectMany(t => new uint[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray(), panel.Data.Source.SkinVertices.ToArray());
        byte[] solid = Mesh(panel.Data.RigidPositions, panel.Data.RigidIndices, null);
        string hostHash = CoverageBuildInputJson.Hash(host), solidHash = CoverageBuildInputJson.Hash(solid);
        var map = new HostCoverageMap(hostHash, panel.Data.Map.Topology, panel.Data.Map.Regions, panel.Data.Map.Refinement, panel.Data.Map.Authoring);
        byte[] attachment = AttachmentSetJson.Write(new([new RigidAttachmentPartDefinition(1, new("support", solidHash), "joint-0", Matrix4x4.Identity)]));
        byte[] companion = EquipmentCoverageJson.Write(new(CoverageBuildInputJson.Hash(attachment), HostCoverageMapJson.SemanticSha256(map),
            [new(23, 1, "r0", Matrix4x4.Identity, [new(0, 0, 0, 4)], .4f, -2, 2, .02f, .5f)]));
        Configuration = new()
        {
            ["formatVersion"] = 1,
            ["attachment"] = Save("attachment.json", attachment),
            ["companion"] = Save("companion.json", companion),
            ["parts"] = new JsonArray(new JsonObject
            { ["id"] = 1, ["asset"] = Save("support.glb", solid), ["hostJoint"] = "joint-0", ["socket"] = Matrix(Matrix4x4.Identity), ["bodyAlignment"] = Matrix(Matrix4x4.Identity) }),
        };
        Manifest = new()
        {
            ["formatVersion"] = 1,
            ["host"] = Save("host.glb", host),
            ["map"] = Save("map.json", HostCoverageMapJson.Write(map)),
            ["configuration"] = Save("configuration.json", Encoding.UTF8.GetBytes(Configuration.ToJsonString())),
            ["inverseBind"] = new JsonArray(Matrix(Matrix4x4.Identity), Matrix(Matrix4x4.Identity)),
            ["stateSchemaSha256"] = CoverageBuildFixture.Hash,
            ["domains"] = new JsonArray(new JsonObject
            { ["id"] = "clip", ["kind"] = "authored-clip", ["authoredFrameIds"] = new JsonArray("frame-a", "frame-b"), ["blendPathIds"] = new JsonArray() }),
            ["candidateSampleIds"] = new JsonArray("a", "b"),
            ["samples"] = new JsonArray(Pose("a", "frame-a", 1, Matrix4x4.Identity, panel.Palette), Pose("b", "frame-b", 2, Matrix4x4.CreateScale(1, 1, 2), panel.Palette)),
        };
        Write();
    }
    internal void Write() => File.WriteAllText(ManifestPath, Manifest.ToJsonString());
    internal void WriteConfiguration()
    {
        Manifest["configuration"] = Save("configuration.json", Encoding.UTF8.GetBytes(Configuration.ToJsonString()));
        Write();
    }
    internal JsonObject Save(string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(Folder, name), bytes);
        return new() { ["path"] = name, ["sha256"] = CoverageBuildInputJson.Hash(bytes) };
    }
    static JsonObject Pose(string id, string frame, ulong key, Matrix4x4 equipment, Matrix4x4[] palette) => new()
    {
        ["id"] = id,
        ["domainId"] = "clip",
        ["authoredFrameId"] = frame,
        ["stateKey"] = key,
        ["actorModel"] = Matrix(Matrix4x4.Identity),
        ["palette"] = new JsonArray(palette.Select(m => (JsonNode)Matrix(m)).ToArray()),
        ["commands"] = new JsonArray(new JsonObject
        { ["id"] = 23, ["equipmentInstanceId"] = 17, ["equipmentWorld"] = Matrix(equipment), ["coverageWorld"] = Matrix(equipment), ["transitionWeight"] = 1 }),
    };
    internal static JsonArray Matrix(Matrix4x4 m) => new(new float[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    static byte[] Mesh(Vector3[] positions, uint[] indices, SkinnedVertex[]? skin)
    {
        var fixture = new SkinnedAttachmentGlbFixture(jointCount: 2);
        var prim = fixture.Primitives[0]!.AsObject();
        prim["attributes"] = new JsonObject();
        Attribute("POSITION", "VEC3", positions.Select(p => new float[] { p.X, p.Y, p.Z }).ToArray());
        Attribute("NORMAL", "VEC3", positions.Select(_ => new float[] { 0, 0, 1 }).ToArray());
        if (skin != null)
        {
            Attribute("JOINTS_0", "VEC4", skin.Select(v => new float[] { v.BoneIndices.X, v.BoneIndices.Y, v.BoneIndices.Z, v.BoneIndices.W }).ToArray());
            Attribute("WEIGHTS_0", "VEC4", skin.Select(v => new float[] { v.BoneWeights.X, v.BoneWeights.Y, v.BoneWeights.Z, v.BoneWeights.W }).ToArray());
        }
        else
        {
            fixture.Root.Remove("skins");
            fixture.Root["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 });
            fixture.Root["scenes"]![0]!["nodes"] = new JsonArray(0);
        }
        byte[] indexBytes = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(i * 4), indices[i]);
        prim["indices"] = fixture.Accessors.Count;
        fixture.Accessors.Add(new JsonObject { ["bufferView"] = fixture.AddView(indexBytes), ["componentType"] = 5125, ["count"] = indices.Length, ["type"] = "SCALAR" });
        return fixture.Build();
        void Attribute(string name, string type, float[][] values)
        {
            bool joints = name == "JOINTS_0";
            byte[] bytes = new byte[values.Length * values[0].Length * (joints ? 2 : 4)];
            int offset = 0;
            foreach (var row in values)
                foreach (float value in row)
                {
                    if (joints) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
                    else BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
                    offset += joints ? 2 : 4;
                }
            var accessor = new JsonObject { ["bufferView"] = fixture.AddView(bytes), ["componentType"] = joints ? 5123 : 5126, ["count"] = values.Length, ["type"] = type };
            if (name == "POSITION")
            {
                accessor["min"] = new JsonArray(positions.Min(p => p.X), positions.Min(p => p.Y), positions.Min(p => p.Z));
                accessor["max"] = new JsonArray(positions.Max(p => p.X), positions.Max(p => p.Y), positions.Max(p => p.Z));
            }
            prim["attributes"]![name] = fixture.Accessors.Count;
            fixture.Accessors.Add(accessor);
        }
    }
    public void Dispose() => Directory.Delete(Folder, true);
}
