using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Gpu;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;

namespace KhaozEngine.Tests.Render3D;

internal sealed class CoverageBindingFixture : IDisposable
{
    readonly AttachmentAssetHarness harness;
    internal Scene3D Scene => harness.Scene;
    internal readonly AttachmentTestSource Source = new();
    internal readonly AttachmentRecordingScene Adapter;
    internal readonly AttachmentSetCache Cache;
    internal readonly SkinnedGltfMesh[] Meshes;
    internal readonly SkinnedMeshHandle[] Hosts;
    internal readonly Skeleton Skeleton;
    internal readonly Matrix4x4[] Palette;
    internal readonly ContractJointMap Map;
    internal readonly LoadedAttachmentSet Loaded;
    internal readonly BoundAttachmentSet Bound;
    internal readonly LoadedEquipmentCoverage Coverage;
    internal readonly CoverageRefinement Topology;
    internal readonly MotionKey Actor = MotionKey.From(901);
    internal readonly CoverageRecessionTable Table;
    internal static RigidInstanceDraw Template => MixedAttachmentSubmissionFixture.Template;

    internal CoverageBindingFixture(bool mixed = false, bool materialSlices = false, IGpuDevice? device = null)
    {
        harness = new(device);
        byte[] host = CoverageImportTests.HostFixture().Build();
        Meshes = GltfLoader.LoadSkinnedPartsWithMaterials(host, "binding-host", CoverageImportTests.Map(host)).Select(p => p.Mesh).ToArray();
        Skeleton = Meshes[0].Skeleton!;
        Palette = Skeleton.ComposeRestPose();
        Map = new(Skeleton, new SkeletonContract([
            new("joint-0", null, false), new("joint-1", "joint-0", true),
            new("joint-2", "joint-0", true), new("joint-3", "joint-0", true)]));
        Hosts = Meshes.Select(Scene.LoadSkinnedMesh).ToArray();
        Topology = CoverageRefinement.Build(Meshes[0].Coverage!.Source, Meshes[0].Coverage!.Map);
        Adapter = new(Scene);
        Cache = new(Adapter, Source);
        var panel = CoverageFixture.CurvedPanel(false);
        var mesh = Source.Mesh("support.glb", Solid(panel.RigidPositions, panel.RigidIndices, materialSlices));
        var parts = new System.Collections.Generic.List<AttachmentPartDefinition>
        { new RigidAttachmentPartDefinition(1, mesh, "joint-0", Matrix4x4.Identity) };
        if (mixed) parts.Add(new SkinnedAttachmentPartDefinition(2, Source.Mesh("garment.glb", new SkinnedAttachmentGlbFixture().Build())));
        string attachment = Source.Add("equipment.json", AttachmentSetJson.Write(new(parts)));
        Loaded = Cache.Get("equipment.json", attachment);
        Bound = Loaded.Bind(Map, Meshes[0]);
        string companion = Source.Add("coverage.json", EquipmentCoverageJson.Write(new(attachment,
            HostCoverageMapJson.SemanticSha256(Meshes[0].Coverage!.Map), [new(23, 1, "patch", Matrix4x4.Identity,
                [new(0, 0, 0, 4)], .4f, -2, 2, .02f, .8f)])));
        Coverage = Cache.GetCoverage(Loaded, "coverage.json", companion);
        var configuration = new System.Collections.Generic.List<CoverageConfigurationPart>
        { new(1, "joint-0", Matrix4x4.Identity, Map.BodyAlignment(Map.Node("joint-0"))) };
        if (mixed) configuration.Add(new(2, string.Empty, Matrix4x4.Identity, Matrix4x4.Identity));
        var identity = new CoverageRecessionIdentity(Meshes[0].Coverage!.Source.SourceSha256,
            HostCoverageMapJson.SemanticSha256(Meshes[0].Coverage!.Map),
            CoverageConfigurationIdentity.Compute([new(attachment, companion, configuration)], Meshes[0].InverseBind),
            CoverageTableBinding.RefinementSha256(Topology), CoverageTableBinding.ArithmeticSha256, CoverageFixture.Hash);
        int[] regions = [0];
        int[] representatives = new int[Topology.PositionGroupCount];
        CoverageReferenceTopology.FillActiveRepresentatives(Topology, regions, representatives, new());
        Table = CoverageRecessionTable.Create(identity, representatives,
            new float[Topology.PositionGroupCount * 2], [new(new(10), 0), new(new(20), 1), new(new(30), -1)]);
    }

    internal CoveredAttachmentInstance Equipment(ulong incarnation = 17) => new(incarnation, Bound, Coverage);
    internal HostCoverageBinding Binding() => new(Adapter, Skeleton, Hosts, Actor, Table);
    internal SkinnedInstanceDraw Host(SkinnedCoverageFrame frame, int slice = 0, Matrix4x4? model = null) =>
        new(Hosts[slice], model ?? Matrix4x4.Identity) { Motion = Actor, Coverage = frame };
    internal bool Prepare(HostCoverageBinding binding, out SkinnedCoverageFrame frame, out CoverageFailure failure,
        ulong key = 10) => binding.TryPrepare(Skeleton, Palette, Matrix4x4.Identity, new(key), Template, out frame, out failure);

    internal CoverageRecessionTable WithIdentity(CoverageRecessionIdentity identity) =>
        CoverageRecessionTable.Create(identity, Table.Representatives, new float[Table.PositionGroupCount], [new(new(10), 0)]);

    static byte[] Solid(Vector3[] positions, uint[] indices, bool materials)
    {
        var glb = new SkinnedAttachmentGlbFixture();
        glb.Root.Remove("skins");
        glb.Root["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 });
        glb.Root["scenes"]![0]!["nodes"] = new JsonArray(0);
        var primitive = glb.Primitives[0]!;
        byte[] vertices = new byte[positions.Length * 12], faces = new byte[indices.Length * 4];
        for (int i = 0; i < positions.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(i * 12), positions[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(i * 12 + 4), positions[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(i * 12 + 8), positions[i].Z);
        }
        for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(faces.AsSpan(i * 4), indices[i]);
        primitive["attributes"] = new JsonObject { ["POSITION"] = glb.Accessors.Count };
        glb.Accessors.Add(new JsonObject
        {
            ["bufferView"] = glb.AddView(vertices),
            ["componentType"] = 5126,
            ["count"] = positions.Length,
            ["type"] = "VEC3",
            ["min"] = new JsonArray(-1, -1, .02f),
            ["max"] = new JsonArray(1, 1, .2f)
        });
        primitive["indices"] = glb.Accessors.Count;
        glb.Accessors.Add(new JsonObject { ["bufferView"] = glb.AddView(faces), ["componentType"] = 5125, ["count"] = indices.Length, ["type"] = "SCALAR" });
        if (materials)
        {
            glb.Root["materials"] = new JsonArray(new JsonObject(), new JsonObject());
            primitive["material"] = 0;
            glb.Accessors[^1]!["count"] = 12;
            var second = primitive.DeepClone();
            second["material"] = 1;
            second["indices"] = glb.Accessors.Count;
            glb.Accessors.Add(new JsonObject
            {
                ["bufferView"] = glb.AddView(faces[48..]),
                ["componentType"] = 5125,
                ["count"] = indices.Length - 12,
                ["type"] = "SCALAR"
            });
            glb.Primitives.Add(second);
        }
        return glb.Build();
    }

    public void Dispose() { Cache.Dispose(); harness.Dispose(); }
}
