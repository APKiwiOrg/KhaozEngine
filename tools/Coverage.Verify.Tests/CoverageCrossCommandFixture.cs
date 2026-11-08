using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>Two disconnected, valid coverage patches. The full-weight moving command lowers one large triangle
/// through another command's unit box. The box command is at partial weight, with its own patch above both solids.</summary>
internal sealed class CoverageCrossCommandFixture
{
    internal const string Domain = "cross-command";
    internal const uint MovingCommand = 41, BoxCommand = 73;
    internal const ulong MovingEquipment = 101, BoxEquipment = 202;
    readonly CoverageRefinement topology;
    readonly CoverageSupportSurface movingSupport = BoxSupport(new(10, 10, 1), new(0, 0, 2));
    readonly CoverageSupportSurface boxSupport = BoxSupport(Vector3.One, Vector3.Zero);

    internal CoverageCrossCommandFixture()
    {
        var positions = new List<Vector3>();
        var skin = new List<SkinnedVertex>();
        var triangles = new List<CoverageSourceTriangle>();
        var regions = new List<HostCoverageRegion>();
        Patch("a-moving", 0, 2, -1, 4);
        Patch("b-box", 1, 4, 4, .25f);
        var source = new CoverageSourceTopology("synthetic-cross-command-host", CoverageBuildFixture.Hash, positions.ToArray(),
            positions.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)), triangles, skin.ToArray());
        var map = new HostCoverageMap(CoverageBuildFixture.Hash,
            positions.Select((_, i) => new CoverageTopologyEntry(0, 0, i, i)), regions, [], new("synthetic", "cross-command"));
        topology = CoverageRefinement.Build(source, map);

        void Patch(string name, int bone, float innerZ, float outerZ, float size)
        {
            int start = positions.Count, first = triangles.Count;
            Vector2[] corners = [new(-size, -.75f * size), new(size, -.75f * size), new(0, 1.5f * size)];
            for (int ring = 0; ring < 2; ring++)
                foreach (var corner in corners)
                {
                    var point = new Vector3(corner * (ring + 1), ring == 0 ? innerZ : outerZ);
                    positions.Add(point);
                    skin.Add(new() { Position = point, Normal = Vector3.UnitZ, Color = Vector4.One,
                        BoneIndices = new(bone, 0, 0, 0), BoneWeights = new(1, 0, 0, 0) });
                }
            Face(0, 1, 2);
            for (int i = 0; i < 3; i++)
            {
                int next = (i + 1) % 3;
                Face(i, i + 3, next + 3);
                Face(i, next + 3, next);
            }
            regions.Add(new(name, [new(new(0, 0, first, 1), CoverageTriangleRole.Core),
                new(new(0, 0, first + 1, 6), CoverageTriangleRole.Collar)],
                Enumerable.Range(0, 6).Select(i => new CoverageSurfaceStrength(start + i, i < 3 ? 1 : 0))));
            void Face(int a, int b, int c) => triangles.Add(new(0, 0, triangles.Count, -1, 0, start + a, start + b, start + c));
        }
    }

    internal CoverageBuildInput Input(bool crossing)
    {
        var control = Pose(false, "control");
        var current = Pose(crossing, "current");
        return new(topology, [Matrix4x4.Identity, Matrix4x4.Identity],
            [new(Domain, CoverageDomainKind.AuthoredClip, ["frame"], [], [new(0, 0, 1, current, false, false)])],
            [control], ["control"], new(CoverageBuildFixture.Hash, CoverageBuildFixture.Hash, CoverageBuildFixture.Hash,
                CoverageBuildFixture.Hash, CoverageBuildFixture.Hash, CoverageBuildFixture.Hash), CoverageBuildFixture.Hash);
    }

    CoverageSample Pose(bool crossing, string id)
    {
        var movingWorld = Matrix4x4.CreateTranslation(crossing ? 0 : 20, 0, 0);
        return new(id, Domain, "frame", null, new(1), Matrix4x4.Identity, [movingWorld, Matrix4x4.Identity],
            [new(MovingCommand, MovingEquipment, topology, 0, movingSupport, movingWorld, movingWorld, -10, 10, .4f, .5f, 4),
                new(BoxCommand, BoxEquipment, topology, 1, boxSupport, Matrix4x4.Identity, Matrix4x4.Identity, -10, 10, .4f, .05f, 6, .5f)]);
    }

    internal static CoverageRecessionTable Table(CoverageBuildInput input)
    {
        int[] representatives = CoverageSampleVerifier.Representatives(input.Topology, input.Samples[0]);
        float[] scalars = representatives.Select(v => v >= 0 && input.Topology.Vertices[v].RegionIndex == 0 ? 2f : 0f).ToArray();
        return CoverageRecessionTable.Create(input.Identity, representatives, scalars, [new(new(1), 0)]);
    }

    /// <summary>Exact dyadic position oracle for the stored row. Replay separately certifies its actual gathered records.</summary>
    internal static (ModelVertex[] Raw, ModelVertex[] Final) KnownPositions(CoverageBuildInput input, CoverageRecessionTable table)
    {
        var sample = input.Domains[0].DenseRows[0].Sample;
        var raw = new ModelVertex[input.Topology.VertexCount];
        var palette = sample.Palette.ToArray();
        var source = input.Topology.Source.SkinVertices.ToArray().Select(v => SkinningMath.SkinVertex(v, palette)).ToArray();
        if (!input.Topology.TryInterpolate(source, raw, out var failure)) throw new InvalidOperationException(failure.ToString());
        var final = raw.ToArray();
        for (int i = 0; i < final.Length; i++) final[i].Position.Z -= table.Entry(0)[input.Topology.Vertices[i].PositionGroup];
        return (raw, final);
    }

    internal void Write(CoverageBuildFiles files, bool crossing)
    {
        var source = topology.Source;
        byte[] host = CoverageBuildFiles.Mesh(source.Positions.ToArray(), source.Triangles.SelectMany(t => new uint[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray(), source.SkinVertices.ToArray());
        var map = new HostCoverageMap(CoverageBuildInputJson.Hash(host), topology.Map.Topology, topology.Map.Regions, [], topology.Map.Authoring);
        byte[][] solids = new[] { movingSupport, boxSupport }.Select(s => CoverageBuildFiles.Mesh(s.Source.Positions.ToArray(),
            s.Source.Triangles.SelectMany(t => new uint[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray(), null)).ToArray();
        byte[] attachment = AttachmentSetJson.Write(new(solids.Select((s, i) => new RigidAttachmentPartDefinition((uint)i + 1,
            new($"solid-{i}", CoverageBuildInputJson.Hash(s)), $"joint-{i}", Matrix4x4.Identity))));
        byte[] companion = EquipmentCoverageJson.Write(new(CoverageBuildInputJson.Hash(attachment), HostCoverageMapJson.SemanticSha256(map),
            [new(MovingCommand, 1, "a-moving", Matrix4x4.Identity, [new(0, 0, 0, 2)], .4f, -10, 10, .5f, 4),
                new(BoxCommand, 2, "b-box", Matrix4x4.Identity, [new(0, 0, 0, 2)], .4f, -10, 10, .05f, 6)]));
        files.Configuration["attachment"] = files.Save("cross-attachment.json", attachment);
        files.Configuration["companion"] = files.Save("cross-companion.json", companion);
        files.Configuration["parts"] = new JsonArray(solids.Select((s, i) => (JsonNode)new JsonObject
        { ["id"] = i + 1, ["asset"] = files.Save($"cross-solid-{i}.glb", s), ["hostJoint"] = $"joint-{i}",
            ["socket"] = CoverageBuildFiles.Matrix(Matrix4x4.Identity), ["bodyAlignment"] = CoverageBuildFiles.Matrix(Matrix4x4.Identity) }).ToArray());
        files.Manifest["host"] = files.Save("cross-host.glb", host);
        files.Manifest["map"] = files.Save("cross-map.json", HostCoverageMapJson.Write(map));
        files.Manifest["configuration"] = files.Save("cross-config.json", Encoding.UTF8.GetBytes(files.Configuration.ToJsonString()));
        files.Manifest["candidateSampleIds"] = new JsonArray("control");
        files.Manifest["samples"] = new JsonArray(JsonPose(Pose(false, "control"), true));
        files.Manifest["domains"] = new JsonArray(new JsonObject
        {
            ["id"] = Domain, ["kind"] = "authored-clip", ["authoredFrameIds"] = new JsonArray("frame"), ["blendPathIds"] = new JsonArray(),
            ["denseRows"] = new JsonArray(new JsonObject
            { ["denseIndex"] = 0, ["precedingPhaseTick"] = 0, ["resolvedStateKey"] = 1, ["looped"] = false, ["clamped"] = false,
                ["sample"] = JsonPose(Pose(crossing, "current"), false) }),
        });
        files.Write();
    }

    static JsonObject JsonPose(CoverageSample pose, bool withKey)
    {
        var result = new JsonObject
        {
            ["id"] = pose.Id, ["domainId"] = Domain, ["authoredFrameId"] = "frame", ["actorModel"] = CoverageBuildFiles.Matrix(pose.ActorModel),
            ["palette"] = new JsonArray(pose.Palette.ToArray().Select(m => (JsonNode)CoverageBuildFiles.Matrix(m)).ToArray()),
            ["commands"] = new JsonArray(pose.Commands.ToArray().Select(c => (JsonNode)new JsonObject
            { ["id"] = c.Id, ["equipmentInstanceId"] = c.EquipmentInstanceId, ["equipmentWorld"] = CoverageBuildFiles.Matrix(c.EquipmentWorld),
                ["coverageWorld"] = CoverageBuildFiles.Matrix(c.CoverageWorld), ["transitionWeight"] = c.TransitionWeight }).ToArray()),
        };
        if (withKey) result["stateKey"] = pose.StateKey.Value;
        return result;
    }

    internal static CoverageSupportSurface BoxSupport(Vector3 halfSize, Vector3 center)
    {
        Vector3[] points = [new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
        points = points.Select(p => p * halfSize + center).ToArray();
        int[] indices = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 5, 1, 0, 4, 5, 1, 6, 2, 1, 5, 6, 2, 7, 3, 2, 6, 7, 3, 4, 0, 3, 7, 4];
        var source = new CoverageSourceTopology("synthetic-cross-command-solid", CoverageBuildFixture.Hash, points,
            points.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            Enumerable.Range(0, indices.Length / 3).Select(i => new CoverageSourceTriangle(0, 0, i, -1, 0, indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2])));
        return CoverageSupportSurface.Compile(source, [new(0, 0, 0, 2)], Matrix4x4.Identity);
    }
}
