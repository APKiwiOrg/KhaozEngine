using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfProfileRow(string Label, string BaselineSceneId, string CandidateSceneId,
    RendererPerfLayout Layout, int? Iteration, ImmutableDictionary<string, RendererPerfDelta> Deltas);

internal static class RendererPerfProfileComparison
{
    internal static ImmutableArray<RendererPerfProfileRow> Compare(RendererPerfSummary summary)
    {
        var rows = ImmutableArray.CreateBuilder<RendererPerfProfileRow>();
        foreach (var pair in summary.Raw.Manifest.Schedule.ProfilePairs)
            foreach (var layout in new[] { RendererPerfLayout.TotalOnly, RendererPerfLayout.PerPass })
            {
                string baseline = $"S{pair.BaselineScene}", candidate = $"S{pair.CandidateScene}";
                void Add(int? iteration, ImmutableDictionary<string, RendererPerfDistribution> before,
                    ImmutableDictionary<string, RendererPerfDistribution> after)
                    => rows.Add(new(pair.Label, baseline, candidate, layout, iteration,
                        RendererPerfDeltaArithmetic.Compare(before, after, before.Keys.Intersect(after.Keys), null, out _)));
                foreach (var before in summary.Blocks.Where(b => b.Block.SceneId == baseline && b.Block.Layout == layout)
                    .OrderBy(b => b.Block.Iteration))
                {
                    var after = summary.Blocks.SingleOrDefault(b => b.Block.SceneId == candidate && b.Block.Layout == layout
                        && b.Block.Iteration == before.Block.Iteration);
                    if (after is not null) Add(before.Block.Iteration, before.Metrics, after.Metrics);
                }
                var combinedBefore = summary.Combined.SingleOrDefault(b => b.SceneId == baseline && b.Layout == layout);
                var combinedAfter = summary.Combined.SingleOrDefault(b => b.SceneId == candidate && b.Layout == layout);
                if (combinedBefore is not null && combinedAfter is not null) Add(null, combinedBefore.Metrics, combinedAfter.Metrics);
            }
        return rows.ToImmutable();
    }
}
