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
    internal CoverageEvidenceManifest(CoverageBuildInput input, CoverageRecessionTable table,
        IEnumerable<CoveragePairResult> pairs, IEnumerable<CoverageSelectedClaim> claims,
        IEnumerable<CoverageHole> holes, IEnumerable<CoverageCandidateEvidence>? candidates = null, IEnumerable<CoverageEntryEvidence>? entries = null)
    {
        Identity = input.Identity; InputSha256 = input.InputSha256;
        TableSha256 = CoverageBuildInputJson.Hash(CoverageRecessionTableBinary.Write(table));
        ToolCodeSha256 = CoverageBuildInputJson.CodeHash();
        Domains = Array.AsReadOnly(input.Domains.Select(d => new CoverageDomainEvidence(d.Id, d.Kind, d.AuthoredFrameIds,
            d.BlendPathIds, Array.AsReadOnly(d.DenseRows.Select(DenseEvidence).ToArray()))).ToArray());
        CandidateSampleIds = input.CandidateSampleIds;
        Samples = Array.AsReadOnly(input.Samples.Select(s => new CoverageSampleInventory(s.Id, s.DomainId, s.AuthoredFrameId, s.BlendPathId, s.StateKey.Value)).ToArray());
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
    static CoverageDenseRowEvidence DenseEvidence(CoverageDenseRow row)
    {
        var pose = row.Sample;
        return new(row.DenseIndex, row.PrecedingPhaseTick, row.ResolvedStateKey,
            new(pose.Id, pose.DomainId, pose.AuthoredFrameId, pose.BlendPathId, CoverageBuildInputJson.Values(pose.ActorModel),
                Array.AsReadOnly(pose.Palette.ToArray().Select(CoverageBuildInputJson.Values).ToArray()),
                Array.AsReadOnly(pose.Commands.ToArray().Select(c => new CoverageCommandPoseEvidence(c.Id, c.EquipmentInstanceId,
                    CoverageBuildInputJson.Values(c.EquipmentWorld), CoverageBuildInputJson.Values(c.CoverageWorld), c.TransitionWeight)).ToArray())), row.Looped, row.Clamped);
    }
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
    public byte[] Write() => Write(CoverageBuildInputJson.MaxBytes);

    internal byte[] Write(int byteLimit)
    {
        using var stream = new EvidenceStream(byteLimit);
        JsonSerializer.Serialize(stream, this, Options);
        return stream.ToArray();
    }

    /// <summary>A resumed output must bind the same exact inputs, tool, arithmetic and serialized table.
    /// Malformed JSON or field types retain the tool's malformed-input refusal family.</summary>
    internal void ValidateResume(byte[] bytes, byte[]? expectedBytes = null)
    {
        try
        {
            CoverageBuildInputJson.CheckByteCapacity(bytes.LongLength, CoverageBuildInputJson.MaxBytes);
            using var document = CoverageBuildInputJson.Document(bytes);
            var root = document.RootElement;
            using var expectedDocument = CoverageBuildInputJson.Document(expectedBytes ?? Write());
            var expected = expectedDocument.RootElement;
            ValidateObjectTypes(root, expected, "$");
            foreach (string member in new[] { "formatVersion", "tool", "scope", "identity", "inputSha256", "tableSha256", "toolCodeSha256" })
            {
                if (!JsonElement.DeepEquals(root.GetProperty(member), expected.GetProperty(member)))
                    throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ProvenanceMismatch, "Resumed evidence provenance differs.");
            }
        }
        catch (JsonException error)
        {
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidJson, "Malformed resumed evidence JSON.", error)
            { Path = error.Path ?? "$" };
        }
    }

    static void ValidateObjectTypes(JsonElement actual, JsonElement expected, string path)
    {
        CoverageBuildInputJson.Members(actual, path, expected.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (var member in expected.EnumerateObject())
        {
            var value = CoverageBuildInputJson.Get(actual, member.Name, path);
            string memberPath = path + "." + member.Name;
            bool boolean = value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && member.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (value.ValueKind != member.Value.ValueKind && !boolean)
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidValue, "Evidence field has the wrong JSON type.")
                { Path = memberPath, MemberName = member.Name };
            if (value.ValueKind == JsonValueKind.Object) ValidateObjectTypes(value, member.Value, memberPath);
        }
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
