using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Fixed actor-owned reference workspace. Warm construction and gathers never grow its arrays.</summary>
internal sealed class CoverageReferenceScratch
{
    internal CoverageReferenceScratch(CoverageRefinement topology, int commandCapacity = CoverageScratch.MaxCommands)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (commandCapacity is < 1 or > CoverageScratch.MaxCommands) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        ReferenceTopology = new(topology);
        FlushedSource = new ModelVertex[topology.SourceVertexCount];
        Before = new ModelVertex[topology.VertexCount];
        After = new ModelVertex[topology.VertexCount];
        Published = new ModelVertex[topology.VertexCount];
        GroupPositions = new Vector3[topology.PositionGroupCount];
        ActiveGroupRepresentatives = new int[topology.PositionGroupCount];
        FullRecessions = new float[topology.PositionGroupCount];
        Corrections = new float[topology.PositionGroupCount];
        FaceCorrections = new float[topology.TriangleCount];
        FaceGuards = new bool[topology.TriangleCount];
        NormalSums = new Vector3[topology.ShadingGroupCount];
        TangentSums = new Vector3[topology.VertexCount];
        Frames = new CoverageReferenceFrame[commandCapacity];
        Order = new int[commandCapacity];
        RegionCommands = new int[topology.Map.Regions.Count];
        ClearanceOverrides = new float[commandCapacity];
        CorrectionClearanceOverrides = new float[commandCapacity];
        CorrectionConversionOverrides = new float[commandCapacity];
    }

    internal float[] ClearanceOverrides { get; }
    internal float[] CorrectionClearanceOverrides { get; }
    internal float[] CorrectionConversionOverrides { get; }
    internal CoverageReferenceArithmetic Arithmetic { get; } = new();
    internal CoverageReferenceArithmetic UniformArithmetic { get; } = new();
    internal CoverageReferenceTopology ReferenceTopology { get; }
    internal ModelVertex[] FlushedSource { get; }
    internal ModelVertex[] Before { get; }
    internal ModelVertex[] After { get; }
    internal ModelVertex[] Published { get; }
    internal Vector3[] GroupPositions { get; }
    internal int[] ActiveGroupRepresentatives { get; }
    internal float[] FullRecessions { get; }
    internal float[] Corrections { get; }
    internal float[] FaceCorrections { get; }
    internal bool[] FaceGuards { get; }
    internal Vector3[] NormalSums { get; }
    internal Vector3[] TangentSums { get; }
    internal CoverageReferenceFrame[] Frames { get; }
    internal int[] Order { get; }
    internal int[] RegionCommands { get; }
    internal int CommandCapacity => Frames.Length;
    internal int ActiveCommands { get; set; }
}
