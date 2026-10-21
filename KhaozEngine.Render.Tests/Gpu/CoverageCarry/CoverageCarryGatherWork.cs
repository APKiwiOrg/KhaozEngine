using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Offline operation inventory over the actual F0 posed rows. Counts are not hardware timings.</summary>
internal static class CoverageCarryGatherWork
{
    internal static object[] Describe(CoverageCarryFixture fixture)
    {
        var rows = new List<object>();
        var input = new CoverageCarryInputs(fixture, 0);
        for (int pose = 0; pose < 2; pose++)
        {
            input.Prepare(pose);
            var scratch = new CoverageReferenceScratch(fixture.Topology);
            var surface = new ModelVertex[fixture.Topology.VertexCount];
            if (!CoverageCarryReference.TryCarry(fixture.Topology, input.Palette, input.Commands, input.Anchor, scratch, surface, out var failure))
                throw new InvalidOperationException(failure.ToString());
            var topology = scratch.ReferenceTopology;
            int moved = 0, gathers = 0, normalCorners = 0, tangentCorners = 0, visited = 0;
            var unique = new HashSet<(int Corner, bool Tangent)>();
            for (int vertex = 0; vertex < surface.Length; vertex++)
            {
                if (!Moved(vertex)) continue;
                moved++;
                if (scratch.Before[vertex].Normal != Vector3.Zero)
                {
                    int group = topology.Vertices[vertex].ShadingGroup;
                    Gather(topology.ShadingCorners.AsSpan(topology.ShadingCornerOffsets[group],
                        topology.ShadingCornerOffsets[group + 1] - topology.ShadingCornerOffsets[group]), false);
                }
                var t = scratch.Before[vertex].Tangent;
                if (new Vector3(t.X, t.Y, t.Z) != Vector3.Zero)
                    Gather(topology.VertexCorners.AsSpan(topology.VertexFaceOffsets[vertex],
                        topology.VertexFaceOffsets[vertex + 1] - topology.VertexFaceOffsets[vertex]), true);
            }
            rows.Add(new { pose, vertices = surface.Length, movedVertices = moved, gathers, visitedCorners = visited,
                contributingNormalCorners = normalCorners, contributingTangentCorners = tangentCorners,
                uniqueCornerDirections = unique.Count, strictUnitEvaluations = normalCorners + tangentCorners + gathers,
                scalarRootAndDivideJobs = 4 * (normalCorners + tangentCorners + gathers),
                removedCandidateTrafficBytes = surface.Length * 128, removedBasisReadBytes = (normalCorners + tangentCorners) * 72 });

            bool Moved(int vertex) => scratch.Before[vertex].Position != surface[vertex].Position;
            void Gather(ReadOnlySpan<int> corners, bool tangent)
            {
                gathers++;
                foreach (int corner in corners)
                {
                    visited++;
                    int member = (int)topology.Indices[corner];
                    if (!Moved(member)) continue;
                    var value = scratch.Before[member];
                    Vector3 original = tangent ? new(value.Tangent.X, value.Tangent.Y, value.Tangent.Z) : value.Normal;
                    if (original == Vector3.Zero) continue;
                    if (tangent) tangentCorners++; else normalCorners++;
                    unique.Add((corner, tangent));
                }
            }
        }
        return rows.ToArray();
    }
}
