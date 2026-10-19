using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The exact constructor's frozen result for one case. FullRecessions and Corrections are per position group.
/// The four per-slot arrays follow active command slot order, which is ascending command ID: the exact frame's
/// ClearanceCoverage and Slack, and the core correction's clearance and conversion terms.</summary>
internal sealed record CoverageExactCapture(ModelVertex[] Before, ModelVertex[] Final, double[] FullRecessions, double[] Corrections,
    double[] ClearanceCoverage, double[] SlackCoverage, double[] CorrectionClearance, double[] CorrectionConversion, CoverageFailure Failure);

internal sealed record CoverageExactManifestCase(string Id, string Family, int Level, float Weight, string File);

internal sealed record CoverageExactManifest(string Format, string ConstructorBase, string CaptureCommit, CoverageExactManifestCase[] Cases);

/// <summary>Reads immutable exact construction captures and checks their input provenance and geometric witnesses.</summary>
public sealed class CoverageExactCaptureTests
{
    internal const string ConstructorBase = "c525aa2bb52a75db2b4b9c0d17a31638b7938699";
    const string ManifestName = "manifest.json";
    static readonly ConcurrentDictionary<string, Lazy<CoverageExactCapture>> captures = new(StringComparer.Ordinal);

    public static TheoryData<string> Cases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var spec in CoverageReferenceCases.Specs) data.Add(spec.Id);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Exact_capture_manifest_has_72_distinct_cases_and_preserves_live_input_provenance(string id)
    {
        CoverageExactManifest manifest = ReadManifest();
        CoverageExactManifestCase[] cases = manifest.Cases;
        int[] levels = cases.Select(c => c.Level).Distinct().Order().ToArray();
        float[] weights = cases.Select(c => c.Weight).Distinct().Order().ToArray();
        Assert.Equal(72, cases.Length);
        Assert.Equal(72, cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { 2, 3, 4 }, levels);
        Assert.Equal(new[] { 0f, .5f, 1f }, weights);
        Assert.Equal("c525aa2bb52a75db2b4b9c0d17a31638b7938699", manifest.ConstructorBase);
        Assert.Equal(40, manifest.CaptureCommit.Length);
        Assert.Equal(CoverageReferenceCases.Specs.Select(s => s.Id), cases.Select(c => c.Id));
        Assert.All(cases, c => Assert.Equal(c.Id + ".json", c.File));

        CoverageReferenceCase frozen = CoverageReferenceCases.Get(id);
        CoverageExactCapture capture = Read(id);
        // Second assertion: the procedural fixtures rebuilt now still produce exactly the frozen inputs.
        CoverageReferenceCase live = CoverageReferenceCases.BuildLive(id).Case;
        Assert.Same(frozen.Topology, live.Topology);
        Assert.Equal(frozen.Palette.Select(CoverageReferenceBits.Matrix), live.Palette.Select(CoverageReferenceBits.Matrix));
        Assert.Equal(frozen.InverseBind.Select(CoverageReferenceBits.Matrix), live.InverseBind.Select(CoverageReferenceBits.Matrix));
        Assert.Equal(CoverageReferenceBits.Matrix(frozen.Model), CoverageReferenceBits.Matrix(live.Model));
        Assert.Equal(ExplicitFloatBits(frozen.Posed), ExplicitFloatBits(live.Posed));
        Assert.Equal(frozen.Commands.Select(Describe), live.Commands.Select(Describe));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Capture_replay_checks_welds_protected_bytes_and_zero_transition(string id)
    {
        CoverageReferenceCase frozen = CoverageReferenceCases.Get(id);
        CoverageRefinement topology = frozen.Topology;
        CoverageExactCapture capture = Read(id);
        Assert.Equal(topology.VertexCount, capture.Before.Length);
        Assert.Equal(topology.VertexCount, capture.Final.Length);
        Assert.Equal(topology.PositionGroupCount, capture.FullRecessions.Length);
        Assert.Equal(topology.PositionGroupCount, capture.Corrections.Length);
        Assert.All(new[] { capture.ClearanceCoverage, capture.SlackCoverage, capture.CorrectionClearance, capture.CorrectionConversion },
            slots => Assert.Equal(frozen.Commands.Length, slots.Length));
        Assert.True(capture.Failure.IsSuccess);

        var representative = Enumerable.Repeat(-1, topology.PositionGroupCount).ToArray();
        var unwelded = new List<int>();
        var changedProtected = new List<int>();
        int protectedCount = 0, moved = 0;
        for (int i = 0; i < topology.VertexCount; i++)
        {
            CoverageRefinedVertex record = topology.Vertices[i];
            ref int first = ref representative[record.PositionGroup];
            if (first < 0) first = i;
            else if (!PositionBits(capture.Final[first]).SequenceEqual(PositionBits(capture.Final[i]))
                || !PositionBits(capture.Before[first]).SequenceEqual(PositionBits(capture.Before[i]))) unwelded.Add(i);
            if (record.RegionIndex < 0 || record.Strength == 0)
            {
                protectedCount++;
                if (!ExplicitFloatBits([capture.Before[i]]).SequenceEqual(ExplicitFloatBits([capture.Final[i]]))) changedProtected.Add(i);
            }
            if (!PositionBits(capture.Before[i]).SequenceEqual(PositionBits(capture.Final[i]))) moved++;
        }
        Assert.Empty(unwelded);
        Assert.True(protectedCount > 0);
        Assert.Empty(changedProtected);
        float weight = CoverageReferenceCases.Spec(id).Weight;
        if (weight == 0) Assert.Equal(ExplicitFloatBits(capture.Before), ExplicitFloatBits(capture.Final));
        else Assert.True(moved > 0, $"{id} captured no conformation at weight {weight}.");
    }

    internal static CoverageExactCapture Read(string id) =>
        captures.GetOrAdd(id, key => new Lazy<CoverageExactCapture>(() => Load(key))).Value;

    internal static CoverageExactManifest ReadManifest()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CoverageReferenceCases.FixtureDirectory, ManifestName)));
        JsonElement root = document.RootElement;
        return new(root.GetProperty("Format").GetString()!, root.GetProperty("ConstructorBase").GetString()!,
            root.GetProperty("CaptureCommit").GetString()!, root.GetProperty("Cases").EnumerateArray().Select(c => new CoverageExactManifestCase(
                c.GetProperty("Id").GetString()!, c.GetProperty("Family").GetString()!, c.GetProperty("Level").GetInt32(),
                CoverageReferenceBits.Floats(c.GetProperty("Weight").GetString()!).Single(), c.GetProperty("File").GetString()!)).ToArray());
    }

    internal static uint[] ExplicitFloatBits(ReadOnlySpan<ModelVertex> vertices)
    {
        var bits = new uint[vertices.Length * 16];
        for (int i = 0; i < vertices.Length; i++)
        {
            float[] fields = CoverageReferenceBits.Fields(vertices[i]);
            for (int f = 0; f < fields.Length; f++) bits[i * 16 + f] = BitConverter.SingleToUInt32Bits(fields[f]);
        }
        return bits;
    }

    internal static ulong[] ExplicitDoubleBits(ReadOnlySpan<double> values)
    {
        var bits = new ulong[values.Length];
        for (int i = 0; i < values.Length; i++) bits[i] = BitConverter.DoubleToUInt64Bits(values[i]);
        return bits;
    }

    static uint[] PositionBits(in ModelVertex v) => [BitConverter.SingleToUInt32Bits(v.Position.X),
        BitConverter.SingleToUInt32Bits(v.Position.Y), BitConverter.SingleToUInt32Bits(v.Position.Z)];

    static string Describe(CoverageCommandPose c) => $"{c.Id} {c.EquipmentInstanceId} {c.RegionIndex} {c.Support.Identity} "
        + $"{CoverageReferenceBits.Matrix(c.Support.CoverageLocal)} {CoverageReferenceBits.Matrix(c.EquipmentWorld)} "
        + $"{CoverageReferenceBits.Matrix(c.CoverageWorld)} {CoverageReferenceBits.Scalars(c)}";

    static CoverageExactCapture Load(string id)
    {
        using JsonDocument document = CoverageReferenceCases.Open(id);
        JsonElement capture = document.RootElement.GetProperty("Capture");
        JsonElement f = capture.GetProperty("Failure");
        var code = (CoverageFailureCode)f.GetProperty("Code").GetInt32();
        CoverageFailure failure = code == CoverageFailureCode.None ? default : new(code, f.GetProperty("HostIdentity").GetString(),
            f.GetProperty("EquipmentInstanceId").GetUInt64(), f.GetProperty("CommandId").GetUInt32(), f.GetProperty("Count").GetInt32(),
            f.GetProperty("Limit").GetInt32());
        ModelVertex[] Vertices(string name) => CoverageReferenceCases.Lines(capture.GetProperty(name)).Select(CoverageReferenceBits.Vertex).ToArray();
        double[] Groups(string name) => CoverageReferenceCases.Lines(capture.GetProperty(name)).Select(w => CoverageReferenceBits.Doubles(w).Single()).ToArray();
        double[] Slots(string name) => CoverageReferenceBits.Doubles(capture.GetProperty(name).GetString()!);
        return new(Vertices("Before"), Vertices("Final"), Groups("FullRecessions"), Groups("Corrections"), Slots("ClearanceCoverage"),
            Slots("SlackCoverage"), Slots("CorrectionClearance"), Slots("CorrectionConversion"), failure);
    }

}
