using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

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
            if (depth + 2 > stack.Length) throw new CoverageToolStateException(depth + 2, stack.Length);
            stack[depth++] = node.Right; stack[depth++] = node.Left;
        }
        item = -1;
        return false;
    }
}
