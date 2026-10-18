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

/// <summary>Replays the frozen exact construction reference data bit for bit. The writer is opt-in and runs only when
/// KE_WRITE_COVERAGE_EXACT=1, writing nothing but Fixtures/coverage-exact-v1.</summary>
public sealed class CoverageExactCaptureTests
{
    internal const string WriterGate = "KE_WRITE_COVERAGE_EXACT";
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
    public void Exact_capture_manifest_has_72_distinct_cases_and_replays_current_constructor(string id)
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
        var scratch = new CoverageScratch(frozen.Topology);
        var actual = new ModelVertex[frozen.Topology.VertexCount];
        bool accepted = CoverageEvaluator.TryConstructCandidate(frozen.Topology, frozen.Posed, frozen.Model, frozen.Commands, scratch,
            actual, out CoverageFailure failure);
        Assert.True(accepted, failure.ToString());
        CoverageCommandFrame[] activeFrames = scratch.Frames[..scratch.ActiveCommands];
        Assert.Equal(frozen.Commands.Length, activeFrames.Length);
        Assert.Equal(ExplicitFloatBits(capture.Before), ExplicitFloatBits(scratch.Before));
        Assert.Equal(ExplicitFloatBits(capture.Final), ExplicitFloatBits(actual));
        Assert.Equal(capture.Failure, failure);
        Assert.Equal(ExplicitDoubleBits(capture.FullRecessions), ExplicitDoubleBits(scratch.FullRecessions));
        Assert.Equal(ExplicitDoubleBits(capture.Corrections), ExplicitDoubleBits(scratch.Corrections));
        Assert.Equal(ExplicitDoubleBits(capture.ClearanceCoverage), ExplicitDoubleBits(activeFrames.Select(f => f.ClearanceCoverage).ToArray()));
        Assert.Equal(ExplicitDoubleBits(capture.SlackCoverage), ExplicitDoubleBits(activeFrames.Select(f => f.Slack).ToArray()));
        var (correctionClearance, correctionConversion) = CorrectionTerms(frozen, scratch);
        Assert.Equal(ExplicitDoubleBits(capture.CorrectionClearance), ExplicitDoubleBits(correctionClearance));
        Assert.Equal(ExplicitDoubleBits(capture.CorrectionConversion), ExplicitDoubleBits(correctionConversion));

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

    [Fact]
    public void Exact_capture_writer_is_disabled_without_explicit_environment_gate()
    {
        foreach (string? gate in new[] { null, "", "0", "true", "yes", " 1", "1 " }) Assert.False(WriterEnabled(gate));
        Assert.True(WriterEnabled("1"));
        string elsewhere = Path.Combine(Path.GetTempPath(), $"coverage-exact-{Guid.NewGuid():N}");
        Assert.False(TryWriteReferenceData(null, elsewhere));
        Assert.False(Directory.Exists(elsewhere));
        // An ordinary run of the writer fact leaves the committed fixture directory exactly as it was.
        if (WriterEnabled(Environment.GetEnvironmentVariable(WriterGate))) return;
        string committed = SourceFixtureDirectory();
        string[] Snapshot() => Directory.Exists(committed)
            ? Directory.GetFiles(committed).Order(StringComparer.Ordinal)
                .Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f).Ticks}").ToArray()
            : [];
        string[] before = Snapshot();
        Write_exact_reference_data_when_explicitly_requested();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Write_exact_reference_data_when_explicitly_requested()
    {
        // Opt-in only. Without the gate this returns before building, constructing or writing anything.
        string directory = SourceFixtureDirectory();
        if (!TryWriteReferenceData(Environment.GetEnvironmentVariable(WriterGate), directory)) return;
        Assert.True(File.Exists(Path.Combine(directory, ManifestName)));
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

    /// <summary>The exact core correction's per-slot clearance and conversion, from the same frames and interpolated
    /// surface the construction just used.</summary>
    static (double[] Clearance, double[] Conversion) CorrectionTerms(CoverageReferenceCase c, CoverageScratch scratch)
    {
        var clearance = new double[scratch.ActiveCommands];
        var conversion = new double[scratch.ActiveCommands];
        for (int slot = 0; slot < clearance.Length; slot++)
        {
            ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
            if (!CoverageCoreCorrection.TryTerms(scratch.Before, c.Model, c.Commands[frame.Command], frame,
                out clearance[slot], out _, out conversion[slot]))
                throw new InvalidOperationException($"{c.Id}: exact correction terms refused slot {slot}.");
        }
        return (clearance, conversion);
    }

    static (bool Accepted, CoverageExactCapture Capture) Run(CoverageReferenceCase c)
    {
        var scratch = new CoverageScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        bool accepted = CoverageEvaluator.TryConstructCandidate(c.Topology, c.Posed, c.Model, c.Commands, scratch, final, out CoverageFailure failure);
        if (!accepted) return (false, new([], [], [], [], [], [], [], [], failure));
        CoverageCommandFrame[] frames = scratch.Frames[..scratch.ActiveCommands];
        var (clearance, conversion) = CorrectionTerms(c, scratch);
        return (true, new(scratch.Before.ToArray(), final, scratch.FullRecessions.ToArray(), scratch.Corrections.ToArray(),
            frames.Select(f => f.ClearanceCoverage).ToArray(), frames.Select(f => f.Slack).ToArray(), clearance, conversion, failure));
    }

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

    internal static bool WriterEnabled(string? gate) => gate == "1";

    /// <summary>Captures every case from the live fixtures and replaces the fixture directory's JSON files. Refuses
    /// without the gate, and throws on any refused case rather than omitting it.</summary>
    internal static bool TryWriteReferenceData(string? gate, string directory)
    {
        if (!WriterEnabled(gate)) return false;
        string head = Git(directory, "rev-parse HEAD");
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.GetFiles(directory, "*.json")) File.Delete(stale);
        var rows = new List<(CoverageReferenceSpec Spec, CoverageReferenceCase Case)>();
        foreach (CoverageReferenceSpec spec in CoverageReferenceCases.Specs)
        {
            CoverageReferenceLive live = CoverageReferenceCases.BuildLive(spec.Id);
            var (accepted, capture) = Run(live.Case);
            if (!accepted) throw new InvalidOperationException($"Coverage reference case {spec.Id} was refused: {capture.Failure}");
            File.WriteAllBytes(Path.Combine(directory, spec.Id + ".json"), Json(json =>
            {
                CoverageReferenceCases.WriteInputs(json, live);
                WriteCapture(json, capture);
            }));
            rows.Add((spec, live.Case));
        }
        File.WriteAllBytes(Path.Combine(directory, ManifestName), Json(json =>
        {
            json.WriteString("Format", CoverageReferenceCases.DirectoryName);
            json.WriteString("ConstructorBase", ConstructorBase);
            json.WriteString("CaptureCommit", head);
            json.WriteStartObject("FieldOrder");
            json.WriteString("ModelVertex", CoverageReferenceBits.ModelVertexOrder);
            json.WriteString("SkinnedVertex", CoverageReferenceBits.SkinnedVertexOrder);
            json.WriteString("Matrix4x4", CoverageReferenceBits.MatrixOrder);
            json.WriteString("CommandScalars", CoverageReferenceBits.ScalarOrder);
            json.WriteString("Words", "binary32 as 8 and binary64 as 16 uppercase hex digits, space separated");
            json.WriteEndObject();
            json.WriteStartArray("Cases");
            foreach (var (spec, c) in rows)
            {
                json.WriteStartObject();
                json.WriteString("Id", spec.Id);
                json.WriteString("Family", spec.Family);
                json.WriteNumber("Level", spec.Level);
                json.WriteString("Weight", CoverageReferenceBits.Words([spec.Weight]));
                json.WriteString("File", spec.Id + ".json");
                json.WriteNumber("SourceVertices", c.Topology.SourceVertexCount);
                json.WriteNumber("RefinedVertices", c.Topology.VertexCount);
                json.WriteNumber("Commands", c.Commands.Length);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }));
        return true;
    }

    static void WriteCapture(Utf8JsonWriter json, CoverageExactCapture capture)
    {
        json.WriteStartObject("Capture");
        json.WriteStartObject("Failure");
        json.WriteNumber("Code", (int)capture.Failure.Code);
        json.WriteString("HostIdentity", capture.Failure.HostIdentity);
        json.WriteNumber("EquipmentInstanceId", capture.Failure.EquipmentInstanceId);
        json.WriteNumber("CommandId", capture.Failure.CommandId);
        json.WriteNumber("Count", capture.Failure.Count);
        json.WriteNumber("Limit", capture.Failure.Limit);
        json.WriteEndObject();
        CoverageReferenceCases.Lines(json, "Before", capture.Before.Select(CoverageReferenceBits.Vertex));
        CoverageReferenceCases.Lines(json, "Final", capture.Final.Select(CoverageReferenceBits.Vertex));
        CoverageReferenceCases.Lines(json, "FullRecessions", capture.FullRecessions.Select(d => CoverageReferenceBits.Words([d])));
        CoverageReferenceCases.Lines(json, "Corrections", capture.Corrections.Select(d => CoverageReferenceBits.Words([d])));
        json.WriteString("ClearanceCoverage", CoverageReferenceBits.Words(capture.ClearanceCoverage));
        json.WriteString("SlackCoverage", CoverageReferenceBits.Words(capture.SlackCoverage));
        json.WriteString("CorrectionClearance", CoverageReferenceBits.Words(capture.CorrectionClearance));
        json.WriteString("CorrectionConversion", CoverageReferenceBits.Words(capture.CorrectionConversion));
        json.WriteEndObject();
    }

    static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    static string Git(string directory, string arguments)
    {
        string? existing = directory;
        while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        var start = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = existing ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        string output = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        if (git.ExitCode != 0 || output.Length != 40) throw new InvalidOperationException($"git {arguments} failed: {output}");
        return output;
    }

    static string SourceFixtureDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "Fixtures", CoverageReferenceCases.DirectoryName));
}
