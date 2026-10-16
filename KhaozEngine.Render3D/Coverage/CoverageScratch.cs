using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Actor-owned reusable conformation, certification and normal workspace for one exact compiled topology and
/// bounded command set. Not thread safe. Creation may allocate. Evaluation, validation and normal updates never grow
/// this storage and hold no GPU resources.</summary>
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
        Before = new ModelVertex[topology.VertexCount]; After = new ModelVertex[topology.VertexCount];
        GroupPositions = new Vector3[topology.PositionGroupCount]; GroupStamps = new int[topology.PositionGroupCount];
        GroupParents = new int[topology.PositionGroupCount];
        FullRecessions = new double[topology.PositionGroupCount]; Corrections = new double[topology.PositionGroupCount];
        OverlayHost = new CoverageAffinePoint[3]; OverlaySupport = new CoverageAffinePoint[3];
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
    public int VertexCapacity => Staged.Length;
    public int TriangleCapacity => Topology.TriangleCount;
    public int CommandCapacity { get; }
    internal Vector3[] NormalSums { get; }
    internal Vector3[] TangentSums { get; }
    internal ModelVertex[] Staged { get; }
    /// <summary>Interpolated posed surface before conformation, then the conformed candidate.</summary>
    internal ModelVertex[] Before { get; }
    internal ModelVertex[] After { get; }
    /// <summary>Per position group construction result, valid when its stamp equals the current one.</summary>
    internal Vector3[] GroupPositions { get; }
    internal int[] GroupStamps { get; }
    /// <summary>Full pointwise request and maximum incident core correction, each owned once per position group.</summary>
    internal double[] FullRecessions { get; }
    internal double[] Corrections { get; }
    internal CoverageAffinePoint[] OverlayHost { get; }
    internal CoverageAffinePoint[] OverlaySupport { get; }
    /// <summary>Union-find parents over position groups for connected containment components.</summary>
    internal int[] GroupParents { get; }
    internal byte[] TriangleFlags { get; }
    /// <summary>Region index to sorted command slot, or -1.</summary>
    internal int[] RegionCommands { get; }
    internal CoverageCommandFrame[] Frames { get; }
    /// <summary>Command indices sorted by command ID, so results never depend on submission order.</summary>
    internal int[] Order { get; }
    internal int ActiveCommands { get; set; }
    /// <summary>Optional cost observation of geometry certification, null in ordinary use.</summary>
    internal Action<CoverageStageSpan, bool>? StageObserver { get; set; }
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
