using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>External-only observations of the exact surfaces the oracle constructed. A construction refusal has no
/// after surface. Geometry is never embedded in a test or written without the manifest's explicit output path.</summary>
internal sealed class CoverageResearchSurfaces(CoverageRefinement topology)
{
    readonly List<object> rows = new();

    internal void Add(string key, ModelVertex[] before, ModelVertex[] after, bool constructed, CoverageReferenceScratch scratch, bool prepared)
    {
        rows.Add(new
        {
            key,
            constructed,
            before = Positions(before),
            after = constructed ? Positions(after) : null,
            frames = Enumerable.Range(0, prepared ? scratch.ActiveCommands : 0).Select(s => new
            {
                scratch.Frames[s].ClearanceCoverage,
                scratch.Frames[s].ZScale,
            }).ToArray(),
        });
    }

    internal void Write(string output)
    {
        File.WriteAllText(output + ".surfaces.json", JsonSerializer.Serialize(new
        {
            indices = topology.Indices.ToArray(),
            vertices = topology.Vertices.ToArray().Select(v => new { v.RegionIndex, v.Strength, v.PositionGroup, v.SourceVertex }),
            triangles = topology.Triangles.ToArray().Select(t => new { t.SourceTriangle, t.RegionIndex, role = t.Role.ToString() }),
            anchors = rows,
        }));
    }

    static float[][] Positions(ModelVertex[] vertices) => vertices.Select(v => new[] { v.Position.X, v.Position.Y, v.Position.Z }).ToArray();
}
