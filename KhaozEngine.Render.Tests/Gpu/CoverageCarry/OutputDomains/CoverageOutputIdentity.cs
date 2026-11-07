using System;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageOutputIdentity
{
    internal static bool[] Copies(CoverageCarryAnchor anchor, CoverageCommandPose command)
    {
        var t = anchor.Topology;
        var result = new bool[t.VertexCount];
        for (int v = 0; v < result.Length; v++)
        {
            int g = t.Vertices[v].PositionGroup, representative = anchor.Representatives[g];
            result[v] = representative < 0 || anchor.Recession[g] == 0 || command.TransitionWeight == 0
                || t.Vertices[representative].RegionIndex != command.RegionIndex;
        }
        return result;
    }

    internal static bool Check(CoverageCarryAnchor anchor, CoverageCommandPose command, bool[] claimed) =>
        claimed.Length == anchor.Topology.VertexCount && claimed.SequenceEqual(Copies(anchor, command));
}
