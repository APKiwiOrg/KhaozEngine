using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Admitted canonical roof policy retained with one immutable cluster presentation generation.</summary>
internal sealed class MapRoofCohort
{
    internal MapRoofEvaluationBuffer Evaluation { get; }
    internal IReadOnlyDictionary<string, int> Indices { get; }
    internal MapRoofCohort(NativePropClusterCpuBuild build, MapRoofEvaluationBuffer evaluation)
    {
        Evaluation = evaluation;
        Indices = evaluation.Roofs.Select((r, i) => (r.RoofPlacementId, i))
            .ToDictionary(r => r.RoofPlacementId, r => r.i, StringComparer.Ordinal);
        foreach (string id in build.RepresentedIds)
            if (!Indices.ContainsKey(id)) throw new MapDocumentException("Canonical roof policy omitted a cohort member.");
    }
}
