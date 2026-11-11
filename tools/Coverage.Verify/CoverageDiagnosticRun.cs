using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Non-acceptance observations. Nothing collected here participates in table selection or publication.</summary>
internal sealed class CoverageDiagnosticRun
{
    internal string? Path { get; set; }
    internal CoverageBuildInput? Input { get; set; }
    internal CoverageRecessionTable? Table { get; set; }
    internal CoverageEvidenceManifest? Evidence { get; set; }
    internal CoverageReplayResult? Replay { get; set; }
    internal string? Refusal { get; set; }

    internal byte[] Write(string command, int exit, int byteLimit)
    {
        var input = Input!;
        var document = new CoverageDiagnosticDocument(command, exit, input, Evidence?.TableSha256,
            Refusal);
        document.Add("runRefusals", Refusal == null ? [] : new[] { new { Reason = Refusal } });
        document.Add("domains", input.Domains.Select(d => new { d.Id, d.Kind, d.AuthoredFrameIds, d.BlendPathIds, DenseRowCount = d.DenseRows.Count }));
        document.Add("samples", input.Samples.Select(s => new CoverageSampleInventory(s.Id, s.DomainId, s.AuthoredFrameId, s.BlendPathId, s.StateKey.Value)));
        var constructions = Construct(input);
        document.Add("candidates", constructions);
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
                        CoverageSampleVerifier.Representatives(input.Topology, input.Samples[0]), [],
                        input.Samples.Select(s => s.StateKey.Value).Distinct().Order().Select(k => new CoverageStateEntry(new(k), -1)).ToArray()))
                    : CoverageTableBuilder.Build(new(input.Topology, input.InverseBind, input.Domains, input.Samples, ids,
                        input.Identity, input.InputSha256)).Evidence;
            }
            catch (CoverageBuildValidationException error) { document.Add("analysisRefusals", new[] { new { Reason = error.Reason.ToString(), error.SampleId, error.GroupIndex, error.Failure } }); }
            catch (CoverageToolStateException error) { document.Add("analysisRefusals", new[] { new { Reason = error.Reason.ToString(), error.Required, error.Capacity } }); }
        }
        var evidence = Evidence ?? (command == "build" ? constructed : null);
        document.Add("candidateEntries", constructed?.Entries ?? []);
        document.Add("candidatePairs", constructed?.PairResults ?? []);
        document.Add("entries", evidence?.Entries ?? []);
        document.Add("pairResults", evidence?.PairResults ?? []);
        document.Add("selectedClaims", evidence?.SelectedClaims ?? []);
        document.Add("holes", evidence?.Holes ?? []);
        document.Add("selectedFailures", evidence?.SelectedClaims.Where(c => !c.Valid) ?? []);
        if (command == "verify" && Table != null)
            CoverageReplayDiagnostics.Collect(document, input, Table, Replay);
        return document.Write(byteLimit);
    }

    static CoverageCandidateDiagnostic[] Construct(CoverageBuildInput input)
    {
        var constructor = new CoverageExactConstructor(input.Topology, input.InverseBind);
        var results = new List<CoverageCandidateDiagnostic>();
        int[] expected = CoverageSampleVerifier.Representatives(input.Topology, input.Samples[0]);
        foreach (string id in input.CandidateSampleIds)
        {
            var sample = input.Samples.Single(s => s.Id == id);
            var scalars = new float[input.Topology.PositionGroupCount];
            var reps = new int[scalars.Length];
            try
            {
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
