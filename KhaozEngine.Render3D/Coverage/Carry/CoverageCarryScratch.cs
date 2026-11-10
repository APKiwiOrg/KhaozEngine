using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Actor-owned, cold-reserved skinning, interpolation and ordered-gather workspace. Single threaded.</summary>
internal sealed class CoverageCarryScratch
{
    internal CoverageCarryScratch(CoverageRefinement topology, int boneCount)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        ReferenceTopology = new(topology);
        Skin = new(topology.Source, boneCount);
        PosedSource = new ModelVertex[topology.SourceVertexCount];
        FlushedSource = new ModelVertex[topology.SourceVertexCount];
        BeforeVertices = new ModelVertex[topology.VertexCount];
        After = new ModelVertex[topology.VertexCount];
        Published = new ModelVertex[topology.VertexCount];
        GroupPositions = new Vector3[topology.PositionGroupCount];
        RegionCommands = new int[topology.Map.Regions.Count];
    }

    internal ReadOnlySpan<ModelVertex> Before => BeforeVertices;
    internal CoverageReferenceArithmetic Arithmetic { get; } = new();
    internal CoverageReferenceTopology ReferenceTopology { get; }
    internal CoverageReferenceSkin Skin { get; }
    internal ModelVertex[] PosedSource { get; }
    internal ModelVertex[] FlushedSource { get; }
    internal ModelVertex[] BeforeVertices { get; }
    internal ModelVertex[] After { get; }
    internal ModelVertex[] Published { get; }
    internal Vector3[] GroupPositions { get; }
    internal int[] RegionCommands { get; }
}
