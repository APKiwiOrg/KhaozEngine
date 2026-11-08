using System;
using System.Collections.Generic;
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

/// <summary>Sampled evidence only. This is not a continuous or whole-skin certificate.</summary>
public sealed class CoverageEvidenceManifest
{
    internal CoverageEvidenceManifest(CoverageBuildInput input, CoverageRecessionTable table,
        IEnumerable<CoveragePairResult> pairs, IEnumerable<CoverageSelectedClaim> claims,
        IEnumerable<CoverageHole> holes, IEnumerable<CoverageCandidateEvidence>? candidates = null, IEnumerable<CoverageEntryEvidence>? entries = null)
    {
        Identity = input.Identity; InputSha256 = input.InputSha256;
        TableSha256 = CoverageBuildInputJson.Hash(CoverageRecessionTableBinary.Write(table));
        ToolCodeSha256 = CoverageBuildInputJson.CodeHash();
        Domains = input.Domains;
        CandidateSampleIds = input.CandidateSampleIds;
        Samples = Array.AsReadOnly(input.Samples.Select(s => new CoverageSampleInventory(s.Id, s.DomainId, s.AuthoredFrameId, s.BlendPathId, s.StateKey.Value)).ToArray());
        PairResults = Array.AsReadOnly(pairs.ToArray());
        SelectedClaims = Array.AsReadOnly(claims.ToArray());
        Holes = Array.AsReadOnly(holes.ToArray());
        Candidates = Array.AsReadOnly((candidates ?? []).ToArray());
        Entries = Array.AsReadOnly((entries ?? Enumerable.Range(0, table.EntryCount).Select(i =>
            new CoverageEntryEvidence(i, CoverageBuildInputJson.Hash(CoverageTableBuilder.ScalarBytes(table.Entry(i).ToArray())), i))).ToArray());
        Complete = PairResults.Count > 0 && SelectedClaims.Count == input.Samples.Count && Holes.Count == 0 && SelectedClaims.All(c => c.Valid);
    }
    public int FormatVersion => 1;
    public string Tool => "KhaozEngine.Coverage.Verify/1";
    public string Scope => "declared-samples-only";
    public CoverageRecessionIdentity Identity { get; }
    public string InputSha256 { get; }
    public string TableSha256 { get; }
    public string ToolCodeSha256 { get; }
    public IReadOnlyList<CoverageDomain> Domains { get; }
    public IReadOnlyList<CoverageSampleInventory> Samples { get; }
    public IReadOnlyList<string> CandidateSampleIds { get; }
    public IReadOnlyList<CoverageCandidateEvidence> Candidates { get; }
    /// <summary>Pair EntryIndex addresses this inventory. TableEntryIndex is -1 for an unselected candidate.</summary>
    public IReadOnlyList<CoverageEntryEvidence> Entries { get; }
    public IReadOnlyList<CoveragePairResult> PairResults { get; }
    public IReadOnlyList<CoverageSelectedClaim> SelectedClaims { get; }
    public IReadOnlyList<CoverageHole> Holes { get; }
    public bool Complete { get; }
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
    public byte[] Write() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>A resumed output must bind the same exact inputs, tool, arithmetic and serialized table.</summary>
    internal void ValidateResume(byte[] bytes)
    {
        using var document = CoverageBuildInputJson.Document(bytes);
        var root = document.RootElement;
        using var expectedDocument = CoverageBuildInputJson.Document(Write());
        var expected = expectedDocument.RootElement;
        CoverageBuildInputJson.Members(root, "$", expected.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (string member in new[] { "formatVersion", "tool", "scope", "identity", "inputSha256", "tableSha256", "toolCodeSha256" })
        {
            var actual = CoverageBuildInputJson.Get(root, member, "$");
            if (!JsonElement.DeepEquals(actual, expected.GetProperty(member)))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ProvenanceMismatch, "Resumed evidence provenance differs.");
        }
    }
}
