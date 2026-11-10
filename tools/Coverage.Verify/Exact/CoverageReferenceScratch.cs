using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Offline conformation and correction state layered on the shared carry workspace.</summary>
internal sealed class CoverageReferenceScratch
{
    internal CoverageReferenceScratch(CoverageRefinement topology, int boneCount)
    {
        Carry = new(topology, boneCount);
        Core = new(topology);
        ActiveGroupRepresentatives = new int[topology.PositionGroupCount];
        FullRecessions = new float[topology.PositionGroupCount];
        FaceCorrections = new float[topology.TriangleCount];
        Frames = new CoverageReferenceFrame[CoverageScratch.MaxCommands];
        Supports = new CoverageReferenceSupport?[CoverageScratch.MaxCommands];
        Order = new int[CoverageScratch.MaxCommands];
    }

    internal CoverageCoreFaces Core { get; }
    internal CoverageCarryScratch Carry { get; }
    internal CoverageReferenceArithmetic Arithmetic => Carry.Arithmetic;
    internal CoverageReferenceTopology ReferenceTopology => Carry.ReferenceTopology;
    internal ModelVertex[] Before => Carry.BeforeVertices;
    internal ModelVertex[] After => Carry.After;
    internal Vector3[] GroupPositions => Carry.GroupPositions;
    internal int[] RegionCommands => Carry.RegionCommands;
    internal int[] ActiveGroupRepresentatives { get; }
    internal float[] FullRecessions { get; }
    internal float[] FaceCorrections { get; }
    internal CoverageReferenceFrame[] Frames { get; }
    internal CoverageReferenceSupport?[] Supports { get; }
    internal int[] Order { get; }
    internal int CommandCapacity => Frames.Length;
    internal int ActiveCommands { get; set; }
}
