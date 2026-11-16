using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

public sealed record CoverageEntryEvidence(int EntryIndex, string ScalarSha256, int TableEntryIndex);
public sealed record CoveragePairResult(int EntryIndex, string SampleId, bool Valid, CoverageFailure Failure);
public sealed record CoverageSelectedClaim(ulong StateKey, int EntryIndex, string SampleId, bool Valid, CoverageFailure Failure);
public sealed record CoverageHole(ulong StateKey, IReadOnlyList<string> SampleIds);
public sealed record CoverageSampleInventory(string Id, string DomainId, string? AuthoredFrameId, string? BlendPathId, ulong StateKey);
public sealed record CoverageCandidateEvidence(string SampleId, string ScalarSha256);

public sealed record CoverageCommandPoseEvidence(uint Id, ulong EquipmentInstanceId, float[] EquipmentWorld, float[] CoverageWorld, float TransitionWeight);
public sealed record CoverageDensePoseEvidence(string Id, string DomainId, string? AuthoredFrameId, string? BlendPathId,
    float[] ActorModel, IReadOnlyList<float[]> Palette, IReadOnlyList<CoverageCommandPoseEvidence> Commands);
public sealed record CoverageDenseRowEvidence(int DenseIndex, int PrecedingPhaseTick, ulong ResolvedStateKey,
    CoverageDensePoseEvidence Sample, bool Looped, bool Clamped);
public sealed record CoverageDomainEvidence(string Id, CoverageDomainKind Kind, IReadOnlyList<string> AuthoredFrameIds,
    IReadOnlyList<string> BlendPathIds, IReadOnlyList<CoverageDenseRowEvidence> DenseRows);

/// <summary>Sampled evidence only. Exact sample checks and numerical whole-skin replay are independent gates.
/// Neither is a continuous certificate.</summary>
public sealed class CoverageEvidenceManifest
{
    readonly bool samplesComplete;
    readonly CoverageCampaign input;
    internal CoverageEvidenceManifest(CoverageBuildInput input, CoverageRecessionTable table,
        IEnumerable<CoveragePairResult> pairs, IEnumerable<CoverageSelectedClaim> claims,
        IEnumerable<CoverageHole> holes, IEnumerable<CoverageCandidateEvidence>? candidates = null, IEnumerable<CoverageEntryEvidence>? entries = null)
        : this(CoverageCampaign.FromSnapshot(input), table, pairs, claims, holes, candidates, entries) { }

    internal CoverageEvidenceManifest(CoverageCampaign input, CoverageRecessionTable table,
        IEnumerable<CoveragePairResult> pairs, IEnumerable<CoverageSelectedClaim> claims,
        IEnumerable<CoverageHole> holes, IEnumerable<CoverageCandidateEvidence>? candidates = null, IEnumerable<CoverageEntryEvidence>? entries = null)
    {
        this.input = input;
        Identity = input.Identity; InputSha256 = input.InputSha256;
        TableSha256 = CoverageBuildInputJson.Hash(CoverageRecessionTableBinary.Write(table));
        ToolCodeSha256 = CoverageBuildInputJson.CodeHash();
        Render3DCodeSha256 = CoverageBuildInputJson.Render3DCodeHash();
        Domains = CoverageEvidenceProjection.Domains(input);
        CandidateSampleIds = input.CandidateSampleIds;
        Samples = input.Samples;
        PairResults = Array.AsReadOnly(pairs.ToArray());
        SelectedClaims = Array.AsReadOnly(claims.ToArray());
        Holes = Array.AsReadOnly(holes.ToArray());
        Candidates = Array.AsReadOnly((candidates ?? []).ToArray());
        Entries = Array.AsReadOnly((entries ?? Enumerable.Range(0, table.EntryCount).Select(i =>
            new CoverageEntryEvidence(i, CoverageBuildInputJson.Hash(CoverageTableBuilder.ScalarBytes(table.Entry(i).ToArray())), i))).ToArray());
        samplesComplete = PairResults.Count > 0 && SelectedClaims.Count == input.Samples.Count && Holes.Count == 0 && SelectedClaims.All(c => c.Valid);
    }
    public int FormatVersion => 1;
    public string Tool => "KhaozEngine.Coverage.Verify/1";
    public string Scope => Replay == null ? "declared-samples-only" : "declared-samples-and-dense-whole-skin";
    public CoverageRecessionIdentity Identity { get; }
    public string InputSha256 { get; }
    public string TableSha256 { get; }
    public string ToolCodeSha256 { get; }
    /// <summary>Exact producer assembly bytes for provenance only, independent of arithmetic compatibility.</summary>
    public string Render3DCodeSha256 { get; }
    public IReadOnlyList<CoverageDomainEvidence> Domains { get; }
    public IReadOnlyList<CoverageSampleInventory> Samples { get; }
    public IReadOnlyList<string> CandidateSampleIds { get; }
    public IReadOnlyList<CoverageCandidateEvidence> Candidates { get; }
    /// <summary>Pair EntryIndex addresses this inventory. TableEntryIndex is -1 for an unselected candidate.</summary>
    public IReadOnlyList<CoverageEntryEvidence> Entries { get; }
    public IReadOnlyList<CoveragePairResult> PairResults { get; }
    public IReadOnlyList<CoverageSelectedClaim> SelectedClaims { get; }
    public IReadOnlyList<CoverageHole> Holes { get; }
    public CoverageReplayResult? Replay { get; private set; }
    public bool Complete => samplesComplete && (Replay == null || Replay.Accepted);
    internal void IncludeReplay(CoverageReplayResult replay) => Replay = replay;
    internal CoverageArtifactHeader CreateHeader() => new(CoverageArtifactKind.Evidence, Tool, Scope, InputSha256,
        TableSha256, Identity, ToolCodeSha256, Render3DCodeSha256, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["domains"] = Domains.Count, ["samples"] = Samples.Count, ["candidateSampleIds"] = CandidateSampleIds.Count,
            ["candidates"] = Candidates.Count, ["entries"] = Entries.Count, ["pairResults"] = PairResults.Count,
            ["selectedClaims"] = SelectedClaims.Count, ["holes"] = Holes.Count,
            ["denseRows"] = input.Domains.Sum(d => (long)d.DenseRows.Count), ["replayPoses"] = Replay?.PoseCount ?? 0,
        }, Complete);
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
    public byte[] Write() => Write(CoverageBuildInputJson.MaxBytes);

    internal void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination);
        CoverageEvidenceProjection.WriteTo(writer, this, input, Options);
    }

    internal byte[] Write(int byteLimit)
    {
        using var stream = new EvidenceStream(byteLimit);
        WriteTo(stream);
        return stream.ToArray();
    }

    /// <summary>A resumed output must bind the same exact inputs, tool, arithmetic and serialized table.
    /// Malformed JSON or field types retain the tool's malformed-input refusal family.</summary>
    internal void ValidateResume(byte[] bytes, byte[]? expectedBytes = null)
    {
        CoverageBuildInputJson.CheckByteCapacity(bytes.LongLength, CoverageBuildInputJson.MaxBytes);
        using var actual = new MemoryStream(bytes, writable: false);
        using var expected = new MemoryStream(expectedBytes ?? Write(), writable: false);
        CoverageLogicalDocumentReader.Compare(actual, expected, CoverageArtifactKind.Evidence);
    }

    sealed class EvidenceStream(int byteLimit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CoverageBuildInputJson.CheckByteCapacity(Position + count, byteLimit);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CoverageBuildInputJson.CheckByteCapacity(Position + buffer.Length, byteLimit);
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            CoverageBuildInputJson.CheckByteCapacity(Position + 1, byteLimit);
            base.WriteByte(value);
        }
    }
}
