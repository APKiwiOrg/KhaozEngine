using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Non-acceptance observations. Nothing collected here participates in table selection or publication.</summary>
internal sealed class CoverageDiagnosticRun
{
    CoverageDiagnosticDocument? document;
    (string Command, int Exit) collected;
    internal string? Path { get; set; }
    internal CoverageCampaign? Input { get; set; }
    internal CoverageRecessionTable? Table { get; set; }
    internal CoverageEvidenceManifest? Evidence { get; set; }
    internal CoverageReplayResult? Replay { get; set; }
    internal string? Refusal { get; set; }
    /// <summary>SHA-256 of the exact evidence root bytes resume admission read, recorded as soon as they are read so a
    /// later resume refusal still names them. Null means no existing evidence was read.</summary>
    internal string? ResumedEvidenceSha256 { get; set; }
    /// <summary>True once a transaction carrying this report was attempted. A failed attempt leaves the previous report
    /// in place, so no second report replaces it.</summary>
    internal bool PublicationAttempted { get; set; }

    internal CoverageArtifactHeader CreateHeader(string command, int exit) => CoverageDiagnosticWriter.Header(Collect(command, exit));
    internal void WriteTo(Stream destination, string command, int exit) => Collect(command, exit).WriteTo(destination);
    internal CoverageStagedArtifact Stage(string command, int exit, string scratchDirectory, int artifactByteLimit, int partByteLimit) =>
        CoverageDiagnosticWriter.Stage(Path!, Collect(command, exit), scratchDirectory, artifactByteLimit, partByteLimit);

    /// <summary>Analysis runs numerical work, so the header and the payload of one outcome share one collection.</summary>
    CoverageDiagnosticDocument Collect(string command, int exit)
    {
        if (document != null && collected == (command, exit)) return document;
        var input = Input!;
        var result = new CoverageDiagnosticDocument(command, exit, input, Evidence?.TableSha256, Refusal, ResumedEvidenceSha256);
        result.Add("runRefusals", Refusal == null ? [] : new[] { new { Reason = Refusal } });
        result.Add("domains", CoverageDiagnosticCategory.Project(input.Domains,
            d => new { d.Id, d.Kind, d.AuthoredFrameIds, d.BlendPathIds, DenseRowCount = d.DenseRows.Count }));
        result.Add("samples", input.Samples);
        var constructions = Construct(input);
        result.AddCandidates(constructions);
        // A refused construction cannot erase successfully constructed candidates from the diagnostic matrix.
        // Reuse the builder unchanged with that subset, solely for this non-acceptance document.
        CoverageEvidenceManifest? constructed = command == "build" ? Evidence : null;
        if (constructed == null)
        {
            string[] ids = constructions.Where(c => c.Outcome == "constructed").Select(c => c.SampleId).ToArray();
            try
            {
                constructed = ids.Length == 0
                    ? CoverageSampleVerifier.Verify(input, CoverageRecessionTable.Create(input.Identity,
                        CoverageSampleVerifier.Representatives(input.Topology, input.ReadSample(0)), [],
                        input.Samples.Select(s => s.StateKey).Distinct().Order().Select(k => new CoverageStateEntry(new(k), -1)).ToArray()))
                    : CoverageTableBuilder.Build(input.WithCandidates(ids)).Evidence;
            }
            catch (CoverageBuildValidationException error) { result.Add("analysisRefusals", new[] { new { Reason = error.Reason.ToString(), error.SampleId, error.GroupIndex, error.Failure } }); }
            catch (CoverageToolStateException error) { result.Add("analysisRefusals", new[] { new { Reason = error.Reason.ToString(), error.Required, error.Capacity } }); }
        }
        var evidence = Evidence ?? (command == "build" ? constructed : null);
        result.Add("candidateEntries", constructed?.Entries ?? []);
        result.Add("candidatePairs", constructed?.PairResults ?? []);
        result.Add("entries", evidence?.Entries ?? []);
        result.Add("pairResults", evidence?.PairResults ?? []);
        result.Add("selectedClaims", evidence?.SelectedClaims ?? []);
        result.Add("holes", evidence?.Holes ?? []);
        result.Add("selectedFailures", evidence?.SelectedClaims.Where(c => !c.Valid).ToArray() ?? []);
        if (command == "verify" && Table != null)
            CoverageReplayDiagnostics.Collect(result, input, Table, Replay);
        (document, collected) = (result, (command, exit));
        return result;
    }

    static CoverageCandidateDiagnostic[] Construct(CoverageCampaign input)
    {
        var constructor = new CoverageExactConstructor(input.Topology, input.InverseBind);
        var results = new List<CoverageCandidateDiagnostic>();
        int[] expected = CoverageSampleVerifier.Representatives(input.Topology, input.ReadSample(0));
        var ordinals = input.Samples.Select((sample, index) => (sample.Id, index)).ToDictionary(s => s.Id, s => s.index, StringComparer.Ordinal);
        foreach (string id in input.CandidateSampleIds)
        {
            var scalars = new float[input.Topology.PositionGroupCount];
            var reps = new int[scalars.Length];
            try
            {
                // One candidate pose is decoded at a time and released before the next.
                var sample = input.ReadSample(ordinals[id]);
                if (!constructor.TryConstruct(sample.Palette, sample.ActorModel, sample.Commands,
                    new ModelVertex[input.Topology.VertexCount], scalars, reps, out var failure))
                {
                    results.Add(new(id, "refused", "CandidateConstruction", failure, null));
                    continue;
                }
                CoverageSampleVerifier.CheckRepresentatives(expected, reps, id);
                results.Add(new(id, "constructed", null, null, CoverageBuildInputJson.Hash(CoverageTableBuilder.ScalarBytes(scalars))));
            }
            catch (CoverageBuildValidationException error) { results.Add(new(id, "refused", error.Reason.ToString(), error.Failure, null)); }
            catch (CoverageToolStateException error) { results.Add(new(id, "refused", error.Reason.ToString(), null, null)); }
        }
        return results.ToArray();
    }
}

internal sealed record CoverageCandidateDiagnostic(string SampleId, string Outcome, string? Reason, CoverageFailure? Failure, string? ScalarSha256);
