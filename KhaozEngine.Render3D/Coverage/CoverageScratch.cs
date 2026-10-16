using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Actor-owned reusable normal workspace for one exact compiled topology and bounded command set.
/// Not thread safe. Creation may allocate. Update never grows this storage and holds no GPU resources.</summary>
public sealed class CoverageScratch
{
    public const int MaxCommands = 4;

    public CoverageScratch(CoverageRefinement topology, int commandCapacity = MaxCommands)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (commandCapacity is < 1 or > MaxCommands) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        Topology = topology; CommandCapacity = commandCapacity;
        NormalSums = new Vector3[topology.ShadingGroupCount]; TangentSums = new Vector3[topology.VertexCount];
        Staged = new ModelVertex[topology.VertexCount];
    }

    public CoverageRefinement Topology { get; }
    public int VertexCapacity => Staged.Length;
    public int TriangleCapacity => Topology.TriangleCount;
    public int CommandCapacity { get; }
    internal Vector3[] NormalSums { get; }
    internal Vector3[] TangentSums { get; }
    internal ModelVertex[] Staged { get; }
}
