using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Reusable offline exact certification workspace for one exact compiled topology and
/// bounded command set. Not thread safe. Creation may allocate. Geometry validation never grows
/// this storage and hold no GPU resources.</summary>
public sealed class CoverageScratch
{
    public const int MaxCommands = 4;

    public CoverageScratch(CoverageRefinement topology, int commandCapacity = MaxCommands)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (commandCapacity is < 1 or > MaxCommands) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        Topology = topology; CommandCapacity = commandCapacity;
        GroupPositions = new Vector3[topology.PositionGroupCount]; GroupStamps = new int[topology.PositionGroupCount];
        GroupParents = new int[topology.PositionGroupCount];
        TriangleFlags = new byte[topology.TriangleCount];
        RegionCommands = new int[topology.Map.Regions.Count];
        Frames = new CoverageCommandFrame[commandCapacity]; Order = new int[commandCapacity];
        var bounds = new (Vector3 Min, Vector3 Max)[topology.TriangleCount];
        var indices = topology.Indices;
        for (int t = 0; t < bounds.Length; t++)
        {
            Vector3 a = topology.RestPosition((int)indices[t * 3]), b = topology.RestPosition((int)indices[t * 3 + 1]),
                c = topology.RestPosition((int)indices[t * 3 + 2]);
            bounds[t] = (Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
        }
        Tree = new CoverageRefitBvh(bounds);
    }

    public CoverageRefinement Topology { get; }
    public int VertexCapacity => Topology.VertexCount;
    public int TriangleCapacity => Topology.TriangleCount;
    public int CommandCapacity { get; }
    /// <summary>Per position group certification position, valid when its stamp equals the current one.</summary>
    internal Vector3[] GroupPositions { get; }
    internal int[] GroupStamps { get; }
    /// <summary>Union-find parents over position groups for connected containment components.</summary>
    internal int[] GroupParents { get; }
    internal byte[] TriangleFlags { get; }
    /// <summary>Region index to sorted command slot, or -1.</summary>
    internal int[] RegionCommands { get; }
    internal CoverageCommandFrame[] Frames { get; }
    /// <summary>Command indices sorted by command ID, so results never depend on submission order.</summary>
    internal int[] Order { get; }
    internal int ActiveCommands { get; set; }
    internal CoverageRefitBvh Tree { get; }

    int stamp;

    /// <summary>A fresh nonzero stamp. Wrapping clears every stamp array so stale marks can never match.</summary>
    internal int NextStamp()
    {
        if (++stamp == int.MaxValue)
        {
            Array.Clear(GroupStamps);
            stamp = 1;
        }
        return stamp;
    }
}
