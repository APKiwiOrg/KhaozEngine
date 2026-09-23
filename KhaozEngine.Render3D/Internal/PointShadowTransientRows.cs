using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A static point request that may receive a current-pose transient shadow row.</summary>
internal readonly record struct PointShadowTransientCandidate(
    int RequestIndex, int LightIndex, long StaticKey, float DistanceSquared);

/// <summary>Assign compact rows in a stable viewer-distance and light-identity order.</summary>
internal static class PointShadowTransientRows
{
    internal static int Assign(List<PointShadowTransientCandidate> candidates, int capacity,
        Span<int> rowsByRequest, List<int> selectedRequestIndices)
    {
        rowsByRequest.Fill(-1);
        selectedRequestIndices.Clear();
        candidates.Sort(static (a, b) =>
        {
            int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
            if (distance != 0) return distance;
            int key = a.StaticKey.CompareTo(b.StaticKey);
            return key != 0 ? key : a.LightIndex.CompareTo(b.LightIndex);
        });
        int count = Math.Min(Math.Max(0, capacity), candidates.Count);
        for (int row = 0; row < count; row++)
        {
            int requestIndex = candidates[row].RequestIndex;
            rowsByRequest[requestIndex] = row;
            selectedRequestIndices.Add(requestIndex);
        }
        return count;
    }
}
