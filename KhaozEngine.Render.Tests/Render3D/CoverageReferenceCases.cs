using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>One exact-constructor input: the compiled host, joint palette and inverse binds, the posed source they
/// skin to, the actor model and the posed commands.</summary>
internal sealed record CoverageReferenceCase(string Id, CoverageRefinement Topology, Matrix4x4[] Palette, Matrix4x4[] InverseBind,
    ModelVertex[] Posed, Matrix4x4 Model, CoverageCommandPose[] Commands);

/// <summary>One row of the fixed case matrix. Pose is 0 lifted or 1 bent for cost rows and unused for curved rows.</summary>
internal sealed record CoverageReferenceSpec(string Id, string Family, CoverageCostFixtureMode Mode, int Level, int Pose,
    bool Challenging, bool Seams, float Weight);

/// <summary>The complete rigid equipment source every command support of a case compiles from. Glb is empty for a
/// programmatic solid. Ranges holds each command's inward source triangle ranges.</summary>
internal sealed record CoverageReferenceSupport(string Kind, string Identity, string SourceSha256, byte[] Glb,
    Vector3[] Positions, uint[] Indices, CoverageTriangleRange[][] Ranges);

internal sealed record CoverageReferenceLive(CoverageReferenceCase Case, CoverageReferenceSupport Support);

/// <summary>The frozen exact-constructor case matrix. All and Get return cases rebuilt from the committed input bits in
/// Fixtures/coverage-exact-v1, so later changes to procedural fixture helpers cannot redefine the baseline. Only the
/// compiled host topology is rebuilt live, and loading refuses it unless its identity, counts, fingerprint and skin
/// data match the frozen record. BuildLive runs the procedural fixtures that produced the capture.</summary>
internal static class CoverageReferenceCases
{
    internal const string Cost = "cost", Curved = "curved", Glb = "glb", Solid = "solid";
    internal const string DirectoryName = "coverage-exact-v1";
    internal const ulong CostInstance = 1000;
    const string CostSupportIdentity = "cost-plates.glb";
    internal static readonly int[] Levels = [2, 3, 4];
    internal static readonly float[] Weights = [0, .5f, 1];

    static readonly CoverageReferenceSpec[] specs = BuildSpecs();
    static readonly ConcurrentDictionary<string, Lazy<Host>> hosts = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<CoverageCostFixtureMode, Lazy<CoverageCostGeometry>> geometries = new();
    static readonly ConcurrentDictionary<string, Lazy<CoverageReferenceCase>> frozen = new(StringComparer.Ordinal);

    sealed record Host(CoverageRefinement Topology, CoverageCostGeometry? Cost, CoverageFixture? Curved);

    internal static IReadOnlyList<CoverageReferenceSpec> Specs => specs;
    internal static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", DirectoryName);

    internal static CoverageReferenceCase[] All() => specs.Select(s => Get(s.Id)).ToArray();

    internal static CoverageReferenceCase Get(string id) =>
        frozen.GetOrAdd(id, key => new Lazy<CoverageReferenceCase>(() => LoadFrozen(key))).Value;

    internal static CoverageReferenceSpec Spec(string id) => specs.SingleOrDefault(s => s.Id == id)
        ?? throw new ArgumentException($"Unknown coverage reference case '{id}'.", nameof(id));

    static CoverageReferenceSpec[] BuildSpecs()
    {
        var list = new List<CoverageReferenceSpec>();
        foreach (var mode in new[] { CoverageCostFixtureMode.Typical, CoverageCostFixtureMode.Limit })
            foreach (int level in Levels)
                foreach (int pose in new[] { 0, 1 })
                    foreach (float weight in Weights)
                        list.Add(new($"cost-{(mode == CoverageCostFixtureMode.Typical ? "typical" : "limit")}-l{level}-{(pose == 0 ? "lifted" : "bent")}-{Token(weight)}",
                            Cost, mode, level, pose, false, false, weight));
        foreach (bool challenging in new[] { false, true })
            foreach (bool seams in new[] { false, true })
                foreach (int level in Levels)
                    foreach (float weight in Weights)
                        list.Add(new($"curved-{(challenging ? "challenging" : "ordinary")}-{(seams ? "seams" : "welded")}-l{level}-{Token(weight)}",
                            Curved, default, level, 0, challenging, seams, weight));
        return list.ToArray();
        static string Token(float weight) => weight == 0 ? "w0" : weight == 1 ? "w1" : "w05";
    }

    static Host HostOf(CoverageReferenceSpec spec)
    {
        string key = spec.Family == Cost ? $"cost-{spec.Mode}-{spec.Level}" : $"curved-{spec.Challenging}-{spec.Seams}-{spec.Level}";
        return hosts.GetOrAdd(key, _ => new Lazy<Host>(() =>
        {
            if (spec.Family == Curved)
            {
                var fixture = CoverageFixture.CurvedPanel(spec.Seams, spec.Level, challengingCollar: spec.Challenging);
                return new(CoverageRefinement.Build(fixture.Source, fixture.Map), null, fixture);
            }
            var geometry = geometries.GetOrAdd(spec.Mode, mode => new Lazy<CoverageCostGeometry>(() => CoverageCostGeometry.Build(mode))).Value;
            if (spec.Level == geometry.Level) return new(geometry.Refinement, geometry, null);
            // A level variant keeps the fixture's source and every map field except the refinement level.
            HostCoverageMap m = geometry.Map;
            var map = new HostCoverageMap(m.HostSha256, m.Topology, m.Regions, m.Refinement.Select(r => r with { Level = spec.Level }), m.Authoring);
            return new(CoverageRefinement.Build(geometry.Source, map), geometry, null);
        })).Value;
    }

    /// <summary>The procedural fixture inputs, posed through the same compose then skin path as staging.</summary>
    internal static CoverageReferenceLive BuildLive(string id)
    {
        CoverageReferenceSpec spec = Spec(id);
        Host host = HostOf(spec);
        Matrix4x4[] inverseBind = [Matrix4x4.Identity, Matrix4x4.Identity];
        Matrix4x4 model = Matrix4x4.Identity;
        if (spec.Family == Curved)
        {
            CoverageFixture fixture = host.Curved!;
            Matrix4x4[] pose = CoverageFixture.Pose;
            CoverageSupportSurface plate = CoverageCommands.Plate(fixture);
            CoverageCommandPose[] poses = CoverageCommands.Poses(fixture, host.Topology, plate, model, spec.Weight);
            var solid = new CoverageReferenceSupport(Solid, plate.Source.Identity, plate.Source.SourceSha256, [], plate.Source.Positions.ToArray(),
                Indices(plate.Source), poses.Select(_ => new[] { new CoverageTriangleRange(0, 0, 0, fixture.SupportIndices.Length / 3) }).ToArray());
            return new(new(id, host.Topology, pose, inverseBind, Skin(host.Topology, pose, inverseBind), model, poses), solid);
        }
        CoverageCostGeometry geometry = host.Cost!;
        var palette = new Matrix4x4[2];
        CoverageCostGeometry.Pose(spec.Pose, palette);
        byte[] glb = geometry.EquipmentGlb();
        CoverageSourceTopology source = GltfLoader.LoadCoverageSource(glb, CostSupportIdentity);
        CoverageTriangleRange[][] ranges = geometry.Commands.Select(c => c.InwardSupport
            .Select(r => new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray()).ToArray();
        // The one rigid part is attached to the root joint with an identity local frame.
        Matrix4x4 world = BoneSocket.ComposeRigid(Matrix4x4.Identity, palette[0], model);
        var commands = geometry.Commands.Select((c, k) =>
        {
            var support = CoverageSupportSurface.Compile(source, ranges[k], c.CoverageLocal);
            return new CoverageCommandPose(c.Id, CostInstance, host.Topology, Region(host.Topology, c.HostRegion), support, world,
                support.CoverageLocal * world, c.MinZ, c.MaxZ, c.CollarWidth, c.Clearance, c.MaxWorldRecession, spec.Weight);
        }).ToArray();
        var rigid = new CoverageReferenceSupport(Glb, source.Identity, source.SourceSha256, glb, source.Positions.ToArray(), Indices(source), ranges);
        return new(new(id, host.Topology, palette, inverseBind, Skin(host.Topology, palette, inverseBind), model, commands), rigid);
    }

    static int Region(CoverageRefinement topology, string key)
    {
        for (int i = 0; i < topology.Map.Regions.Count; i++) if (topology.Map.Regions[i].Key == key) return i;
        throw new InvalidDataException($"Coverage reference region '{key}' is absent.");
    }

    static uint[] Indices(CoverageSourceTopology source) => source.Triangles.SelectMany(t => new[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray();

    static ModelVertex[] Skin(CoverageRefinement topology, Matrix4x4[] palette, Matrix4x4[] inverseBind)
    {
        var composed = new Matrix4x4[palette.Length];
        for (int i = 0; i < palette.Length; i++) composed[i] = SkinningMath.Compose(palette[i], inverseBind[i]);
        ReadOnlySpan<SkinnedVertex> source = topology.Source.SkinVertices;
        var posed = new ModelVertex[source.Length];
        for (int i = 0; i < posed.Length; i++) posed[i] = SkinningMath.SkinVertex(source[i], composed);
        return posed;
    }

    /// <summary>SHA-256 over the map's semantic hash, source positions, refined indices, vertex and triangle records.</summary>
    internal static string Fingerprint(CoverageRefinement topology)
    {
        var text = new StringBuilder(HostCoverageMapJson.SemanticSha256(topology.Map)).Append('\n');
        foreach (Vector3 p in topology.Source.Positions) text.Append(CoverageReferenceBits.Words([p.X, p.Y, p.Z])).Append('\n');
        foreach (uint index in topology.Indices) text.Append(index.ToString(CultureInfo.InvariantCulture)).Append(' ');
        text.Append('\n');
        foreach (var v in topology.Vertices)
            text.Append(CultureInfo.InvariantCulture, $"{v.SourceTriangle} {v.SourceVertex} {v.A} {v.B} {v.C} {v.Denominator} {v.PositionGroup} {v.ShadingGroup} {v.RegionIndex} ")
                .Append(CoverageReferenceBits.Words([v.Strength])).Append('\n');
        foreach (var t in topology.Triangles) text.Append(CultureInfo.InvariantCulture, $"{t.SourceTriangle} {t.RegionIndex} {(int)t.Role}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Writes the input half of one case file in fixed field order. The caller writes the capture object.</summary>
    internal static void WriteInputs(Utf8JsonWriter json, CoverageReferenceLive live)
    {
        CoverageReferenceCase c = live.Case;
        CoverageReferenceSpec spec = Spec(c.Id);
        CoverageRefinement topology = c.Topology;
        json.WriteString("Id", c.Id);
        json.WriteString("Family", spec.Family);
        json.WriteString("Mode", spec.Family == Cost ? spec.Mode.ToString() : "");
        json.WriteNumber("Level", spec.Level);
        json.WriteNumber("Pose", spec.Pose);
        json.WriteBoolean("Challenging", spec.Challenging);
        json.WriteBoolean("Seams", spec.Seams);
        json.WriteString("Weight", CoverageReferenceBits.Words([spec.Weight]));
        json.WriteStartObject("Topology");
        json.WriteString("HostIdentity", topology.HostIdentity);
        json.WriteString("SourceSha256", topology.Source.SourceSha256);
        json.WriteString("MapSemanticSha256", HostCoverageMapJson.SemanticSha256(topology.Map));
        json.WriteString("Fingerprint", Fingerprint(topology));
        json.WriteNumber("SourceVertices", topology.SourceVertexCount);
        json.WriteNumber("SourceTriangles", topology.Source.Triangles.Count);
        json.WriteNumber("RefinedVertices", topology.VertexCount);
        json.WriteNumber("RefinedTriangles", topology.TriangleCount);
        json.WriteNumber("PositionGroups", topology.PositionGroupCount);
        json.WriteEndObject();
        Lines(json, "SkinVertices", topology.Source.SkinVertices.ToArray().Select(CoverageReferenceBits.Skin));
        Lines(json, "Palette", c.Palette.Select(CoverageReferenceBits.Matrix));
        Lines(json, "InverseBind", c.InverseBind.Select(CoverageReferenceBits.Matrix));
        json.WriteString("Model", CoverageReferenceBits.Matrix(c.Model));
        Lines(json, "Posed", c.Posed.Select(CoverageReferenceBits.Vertex));
        CoverageReferenceSupport support = live.Support;
        json.WriteStartObject("Support");
        json.WriteString("Kind", support.Kind);
        json.WriteString("Identity", support.Identity);
        json.WriteString("SourceSha256", support.SourceSha256);
        json.WriteString("Glb", Convert.ToHexString(support.Glb));
        Lines(json, "Positions", support.Positions.Select(p => CoverageReferenceBits.Words([p.X, p.Y, p.Z])));
        json.WriteString("Indices", string.Join(' ', support.Indices.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        json.WriteEndObject();
        json.WriteStartArray("Commands");
        for (int k = 0; k < c.Commands.Length; k++)
        {
            CoverageCommandPose command = c.Commands[k];
            json.WriteStartObject();
            json.WriteNumber("Id", command.Id);
            json.WriteNumber("EquipmentInstanceId", command.EquipmentInstanceId);
            json.WriteNumber("RegionIndex", command.RegionIndex);
            Lines(json, "Ranges", support.Ranges[k].Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Mesh} {r.Primitive} {r.FirstTriangle} {r.Count}")));
            json.WriteString("CoverageLocal", CoverageReferenceBits.Matrix(command.Support.CoverageLocal));
            json.WriteString("SupportIdentity", command.Support.Identity);
            json.WriteString("EquipmentWorld", CoverageReferenceBits.Matrix(command.EquipmentWorld));
            json.WriteString("CoverageWorld", CoverageReferenceBits.Matrix(command.CoverageWorld));
            json.WriteString("Scalars", CoverageReferenceBits.Scalars(command));
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    internal static void Lines(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }

    internal static string[] Lines(JsonElement element) => element.EnumerateArray().Select(e => e.GetString()!).ToArray();

    internal static JsonDocument Open(string id) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory, id + ".json")));

    static CoverageReferenceCase LoadFrozen(string id)
    {
        CoverageReferenceSpec spec = Spec(id);
        using JsonDocument document = Open(id);
        JsonElement root = document.RootElement;
        if (root.GetProperty("Id").GetString() != id) throw new InvalidDataException($"{id}: case file names another case.");
        CoverageRefinement topology = HostOf(spec).Topology;
        JsonElement identity = root.GetProperty("Topology");
        void Require(bool condition, string what)
        {
            if (!condition) throw new InvalidDataException($"{id}: the live host differs from the frozen {what}.");
        }
        Require(identity.GetProperty("HostIdentity").GetString() == topology.HostIdentity, "host identity");
        Require(identity.GetProperty("SourceSha256").GetString() == topology.Source.SourceSha256, "source hash");
        Require(identity.GetProperty("MapSemanticSha256").GetString() == HostCoverageMapJson.SemanticSha256(topology.Map), "map identity");
        Require(identity.GetProperty("SourceVertices").GetInt32() == topology.SourceVertexCount
            && identity.GetProperty("SourceTriangles").GetInt32() == topology.Source.Triangles.Count
            && identity.GetProperty("RefinedVertices").GetInt32() == topology.VertexCount
            && identity.GetProperty("RefinedTriangles").GetInt32() == topology.TriangleCount
            && identity.GetProperty("PositionGroups").GetInt32() == topology.PositionGroupCount, "counts");
        Require(identity.GetProperty("Fingerprint").GetString() == Fingerprint(topology), "topology fingerprint");
        Require(Lines(root.GetProperty("SkinVertices")).SequenceEqual(topology.Source.SkinVertices.ToArray().Select(CoverageReferenceBits.Skin)), "skin data");

        Matrix4x4[] palette = Lines(root.GetProperty("Palette")).Select(CoverageReferenceBits.Matrix).ToArray();
        Matrix4x4[] inverseBind = Lines(root.GetProperty("InverseBind")).Select(CoverageReferenceBits.Matrix).ToArray();
        Matrix4x4 model = CoverageReferenceBits.Matrix(root.GetProperty("Model").GetString()!);
        ModelVertex[] posed = Lines(root.GetProperty("Posed")).Select(CoverageReferenceBits.Vertex).ToArray();
        JsonElement supportData = root.GetProperty("Support");
        string kind = supportData.GetProperty("Kind").GetString()!, sourceIdentity = supportData.GetProperty("Identity").GetString()!;
        string sourceSha = supportData.GetProperty("SourceSha256").GetString()!;
        Vector3[] positions = Lines(supportData.GetProperty("Positions")).Select(CoverageReferenceBits.Vector).ToArray();
        int[] indices = supportData.GetProperty("Indices").GetString()!.Split(' ').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        CoverageSourceTopology source = kind == Glb
            ? GltfLoader.LoadCoverageSource(Convert.FromHexString(supportData.GetProperty("Glb").GetString()!), sourceIdentity)
            : new CoverageSourceTopology(sourceIdentity, sourceSha, positions,
                Enumerable.Range(0, positions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)),
                Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0, indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2])));
        if (source.SourceSha256 != sourceSha || !Lines(supportData.GetProperty("Positions")).SequenceEqual(source.Positions.ToArray().Select(p => CoverageReferenceBits.Words([p.X, p.Y, p.Z])))
            || !Indices(source).Select(i => (int)i).SequenceEqual(indices))
            throw new InvalidDataException($"{id}: the frozen support source does not decode to its frozen geometry.");
        var commands = root.GetProperty("Commands").EnumerateArray().Select(command =>
        {
            CoverageTriangleRange[] ranges = Lines(command.GetProperty("Ranges")).Select(r =>
            {
                int[] v = r.Split(' ').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                return new CoverageTriangleRange(v[0], v[1], v[2], v[3]);
            }).ToArray();
            var support = CoverageSupportSurface.Compile(source, ranges, CoverageReferenceBits.Matrix(command.GetProperty("CoverageLocal").GetString()!));
            if (support.Identity != command.GetProperty("SupportIdentity").GetString())
                throw new InvalidDataException($"{id}: a frozen support compiles to another identity.");
            float[] s = CoverageReferenceBits.Floats(command.GetProperty("Scalars").GetString()!);
            return new CoverageCommandPose(command.GetProperty("Id").GetUInt32(), command.GetProperty("EquipmentInstanceId").GetUInt64(), topology,
                command.GetProperty("RegionIndex").GetInt32(), support, CoverageReferenceBits.Matrix(command.GetProperty("EquipmentWorld").GetString()!),
                CoverageReferenceBits.Matrix(command.GetProperty("CoverageWorld").GetString()!), s[0], s[1], s[2], s[3], s[4], s[5]);
        }).ToArray();
        return new(id, topology, palette, inverseBind, posed, model, commands);
    }
}

/// <summary>Explicit binary32 and binary64 hex words in fixed field order, never raw struct bytes or decimal text.</summary>
internal static class CoverageReferenceBits
{
    internal const string ModelVertexOrder = "Position.XYZ Normal.XYZ Color.XYZW Uv.XY Tangent.XYZW";
    internal const string SkinnedVertexOrder = "Position.XYZ Normal.XYZ Color.XYZW Uv.XY BoneIndices.XYZW BoneWeights.XYZW Tangent.XYZW";
    internal const string MatrixOrder = "M11 M12 M13 M14 M21 M22 M23 M24 M31 M32 M33 M34 M41 M42 M43 M44";
    internal const string ScalarOrder = "MinZ MaxZ CollarWidth Clearance MaxWorldRecession TransitionWeight";

    internal static string Words(ReadOnlySpan<float> values)
    {
        var text = new StringBuilder(values.Length * 9);
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(BitConverter.SingleToUInt32Bits(values[i]).ToString("X8", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    internal static string Words(ReadOnlySpan<double> values) =>
        string.Join(' ', values.ToArray().Select(v => BitConverter.DoubleToUInt64Bits(v).ToString("X16", CultureInfo.InvariantCulture)));

    internal static float[] Floats(string text) => text.Length == 0 ? [] : text.Split(' ')
        .Select(w => BitConverter.UInt32BitsToSingle(uint.Parse(w, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))).ToArray();

    internal static double[] Doubles(string text) => text.Length == 0 ? [] : text.Split(' ')
        .Select(w => BitConverter.UInt64BitsToDouble(ulong.Parse(w, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))).ToArray();

    internal static float[] Fields(in ModelVertex v) => [v.Position.X, v.Position.Y, v.Position.Z, v.Normal.X, v.Normal.Y, v.Normal.Z,
        v.Color.X, v.Color.Y, v.Color.Z, v.Color.W, v.Uv.X, v.Uv.Y, v.Tangent.X, v.Tangent.Y, v.Tangent.Z, v.Tangent.W];

    internal static string Vertex(ModelVertex v) => Words(Fields(v));

    internal static ModelVertex Vertex(string text)
    {
        float[] f = Exact(Floats(text), 16);
        return new(new(f[0], f[1], f[2]), new(f[3], f[4], f[5]), new(f[6], f[7], f[8], f[9]), new(f[10], f[11]), new(f[12], f[13], f[14], f[15]));
    }

    internal static string Skin(SkinnedVertex v) => Words([v.Position.X, v.Position.Y, v.Position.Z, v.Normal.X, v.Normal.Y, v.Normal.Z,
        v.Color.X, v.Color.Y, v.Color.Z, v.Color.W, v.Uv.X, v.Uv.Y, v.BoneIndices.X, v.BoneIndices.Y, v.BoneIndices.Z, v.BoneIndices.W,
        v.BoneWeights.X, v.BoneWeights.Y, v.BoneWeights.Z, v.BoneWeights.W, v.Tangent.X, v.Tangent.Y, v.Tangent.Z, v.Tangent.W]);

    internal static string Matrix(Matrix4x4 m) => Words([m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44]);

    internal static Matrix4x4 Matrix(string text)
    {
        float[] f = Exact(Floats(text), 16);
        return new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }

    internal static Vector3 Vector(string text)
    {
        float[] f = Exact(Floats(text), 3);
        return new(f[0], f[1], f[2]);
    }

    internal static string Scalars(in CoverageCommandPose c) =>
        Words([c.MinZ, c.MaxZ, c.CollarWidth, c.Clearance, c.MaxWorldRecession, c.TransitionWeight]);

    static float[] Exact(float[] values, int count) => values.Length == count ? values
        : throw new InvalidDataException($"Expected {count} binary32 words, found {values.Length}.");
}
