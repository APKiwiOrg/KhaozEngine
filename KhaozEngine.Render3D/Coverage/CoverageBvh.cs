using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Immutable bounds hierarchy. Leaf Item is an index into the owning triangle or boundary collection.</summary>
public readonly record struct CoverageBvhNode(Vector3 Min, Vector3 Max, int Left, int Right, int Item);

/// <summary>Load-time bounds tree shared by support queries and later geometric certification.</summary>
public sealed class CoverageBvh
{
    readonly CoverageBvhNode[] nodes;
    internal CoverageBvh((Vector3 Min, Vector3 Max)[] bounds)
    {
        if (bounds.Length == 0) { nodes = Array.Empty<CoverageBvhNode>(); return; }
        var output = new List<CoverageBvhNode>(bounds.Length * 2 - 1);
        Build(Enumerable.Range(0, bounds.Length).ToArray());
        nodes = output.ToArray();
        int Build(int[] items)
        {
            Vector3 min = bounds[items[0]].Min, max = bounds[items[0]].Max;
            foreach (int item in items) { min = Vector3.Min(min, bounds[item].Min); max = Vector3.Max(max, bounds[item].Max); }
            int index = output.Count;
            output.Add(default);
            if (items.Length == 1) output[index] = new(min, max, -1, -1, items[0]);
            else
            {
                Vector3 size = max - min;
                int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
                Array.Sort(items, (a, b) => ((double)bounds[a].Min[axis] + bounds[a].Max[axis]).CompareTo((double)bounds[b].Min[axis] + bounds[b].Max[axis]));
                int half = items.Length / 2;
                int left = Build(items[..half]), right = Build(items[half..]);
                output[index] = new(min, max, left, right, -1);
            }
            return index;
        }
    }
    public ReadOnlySpan<CoverageBvhNode> Nodes => nodes;
}
