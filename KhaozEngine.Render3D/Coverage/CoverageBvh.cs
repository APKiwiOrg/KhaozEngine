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
    internal CoverageBvh((Vector3 Min, Vector3 Max)[] bounds) => nodes = Build(bounds);
    public ReadOnlySpan<CoverageBvhNode> Nodes => nodes;

    /// <summary>Median-split preorder tree. Every child index is larger than its parent's, so a reverse scan refits.</summary>
    internal static CoverageBvhNode[] Build((Vector3 Min, Vector3 Max)[] bounds)
    {
        if (bounds.Length == 0) return Array.Empty<CoverageBvhNode>();
        var output = new List<CoverageBvhNode>(bounds.Length * 2 - 1);
        Split(Enumerable.Range(0, bounds.Length).ToArray());
        return output.ToArray();
        int Split(int[] items)
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
                int left = Split(items[..half]), right = Split(items[half..]);
                output[index] = new(min, max, left, right, -1);
            }
            return index;
        }
    }
}

/// <summary>Actor-owned mutable companion of a cold tree shape. Leaf bounds are rewritten each frame and refit bottom-up
/// without allocation. Cached immutable support hierarchies are never mutated.</summary>
internal sealed class CoverageRefitBvh
{
    readonly CoverageBvhNode[] nodes;
    readonly int[] leafOf;

    internal CoverageRefitBvh((Vector3 Min, Vector3 Max)[] restBounds)
    {
        nodes = CoverageBvh.Build(restBounds);
        leafOf = new int[restBounds.Length];
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].Item >= 0) leafOf[nodes[i].Item] = i;
    }

    internal ReadOnlySpan<CoverageBvhNode> Nodes => nodes;

    /// <summary>Stores an outward float box for one item. An empty box never matches a query.</summary>
    internal void SetLeaf(int item, CoverageBox box)
    {
        ref CoverageBvhNode node = ref nodes[leafOf[item]];
        node = node with { Min = box.LowerFloat, Max = box.UpperFloat };
    }

    internal void SetEmpty(int item)
    {
        ref CoverageBvhNode node = ref nodes[leafOf[item]];
        node = node with { Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity) };
    }

    internal void Refit()
    {
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            ref CoverageBvhNode node = ref nodes[i];
            if (node.Item >= 0) continue;
            node = node with { Min = Vector3.Min(nodes[node.Left].Min, nodes[node.Right].Min), Max = Vector3.Max(nodes[node.Left].Max, nodes[node.Right].Max) };
        }
    }
}

/// <summary>Axis-aligned double box. Conversion to float rounds outward so float node tests stay conservative.</summary>
internal struct CoverageBox
{
    internal double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    internal static CoverageBox Empty => new()
    {
        MinX = double.PositiveInfinity,
        MinY = double.PositiveInfinity,
        MinZ = double.PositiveInfinity,
        MaxX = double.NegativeInfinity,
        MaxY = double.NegativeInfinity,
        MaxZ = double.NegativeInfinity,
    };

    internal void Add(in CoverageVector p)
    {
        MinX = Math.Min(MinX, p.X); MinY = Math.Min(MinY, p.Y); MinZ = Math.Min(MinZ, p.Z);
        MaxX = Math.Max(MaxX, p.X); MaxY = Math.Max(MaxY, p.Y); MaxZ = Math.Max(MaxZ, p.Z);
    }

    internal void Expand(double amount)
    {
        MinX -= amount; MinY -= amount; MinZ -= amount; MaxX += amount; MaxY += amount; MaxZ += amount;
    }

    /// <summary>Drops the Z extent, for projected-sheet hierarchies whose nodes carry zero Z.</summary>
    internal void Flatten() { MinZ = 0; MaxZ = 0; }

    internal Vector3 LowerFloat => new(Lower(MinX), Lower(MinY), Lower(MinZ));
    internal Vector3 UpperFloat => new(Upper(MaxX), Upper(MaxY), Upper(MaxZ));

    static float Lower(double value)
    {
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }

    static float Upper(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }
}

/// <summary>Allocation-free depth-first walk returning leaf items whose float bounds meet a query box.</summary>
internal ref struct CoverageBvhWalk
{
    readonly ReadOnlySpan<CoverageBvhNode> nodes;
    readonly Span<int> stack;
    readonly Vector3 min, max;
    int depth;

    internal CoverageBvhWalk(ReadOnlySpan<CoverageBvhNode> nodes, Span<int> stack, in CoverageBox box)
    {
        this.nodes = nodes; this.stack = stack;
        min = box.LowerFloat; max = box.UpperFloat;
        depth = 0;
        if (nodes.Length > 0) stack[depth++] = 0;
    }

    internal bool Next(out int item)
    {
        while (depth > 0)
        {
            CoverageBvhNode node = nodes[stack[--depth]];
            if (node.Min.X > max.X || node.Max.X < min.X || node.Min.Y > max.Y || node.Max.Y < min.Y
                || node.Min.Z > max.Z || node.Max.Z < min.Z) continue;
            if (node.Item >= 0) { item = node.Item; return true; }
            if (depth + 2 > stack.Length) throw new InvalidOperationException("Coverage hierarchy is deeper than its walk stack.");
            stack[depth++] = node.Right; stack[depth++] = node.Left;
        }
        item = -1;
        return false;
    }
}
