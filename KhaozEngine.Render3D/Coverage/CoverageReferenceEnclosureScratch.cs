using System;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using I = KhaozEngine.Render3D.CoverageFloatInterval;

namespace KhaozEngine.Render3D;

internal enum CoverageEnclosureSite { None, Skinning, Interpolation, QueryHeight, Boundary, Conformation, OverlayHeight, Correction, FullTarget, Normals, FinalStore, HostWorld, EquipmentWorld }

/// <summary>Fixed pose workspace. Published spans remain valid until the next evaluation.</summary>
internal sealed class CoverageReferenceEnclosureScratch
{
    internal CoverageReferenceEnclosureScratch(CoverageRefinement topology, int commandCapacity = CoverageScratch.MaxCommands)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (commandCapacity is < 1 or > CoverageScratch.MaxCommands) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        Rows = new(topology);
        CommandCapacity = commandCapacity;
        Source = new V[topology.SourceVertexCount];
        SourceNormals = new V[Source.Length]; SourceTangents = new V[Source.Length];
        Before = new V[topology.VertexCount]; After = new V[Before.Length];
        Normals = new V[Before.Length]; Tangents = new V[Before.Length];
        Model = new V[Before.Length]; World = new V[Before.Length];
        Groups = new V[topology.PositionGroupCount];
        Full = new I[Groups.Length]; Representatives = new int[Groups.Length];
        Faces = new I[topology.TriangleCount]; FaceGuards = new bool[Faces.Length];
        Bases = new CoverageEnclosureBasis[Faces.Length];
        GroupSites = new CoverageEnclosureSite[Groups.Length];
    }

    internal CoverageReferenceTopology Rows { get; }
    internal int CommandCapacity { get; }
    internal V[] Source { get; }
    internal V[] SourceNormals { get; }
    internal V[] SourceTangents { get; }
    internal V[] Before { get; }
    internal V[] After { get; }
    internal V[] Normals { get; }
    internal V[] Tangents { get; }
    internal V[] Groups { get; }
    internal I[] Full { get; }
    internal I[] Faces { get; }
    internal bool[] FaceGuards { get; }
    internal CoverageEnclosureBasis[] Bases { get; }
    internal int[] Representatives { get; }
    internal CoverageEnclosureSite[] GroupSites { get; }
    internal V[] Model { get; }
    internal V[] World { get; }
    internal bool MayRefuse { get; set; }
    internal CoverageEnclosureSite FirstUnboundedSite { get; set; }
    internal int FirstUnboundedIndex { get; set; }
    internal int FirstUnboundedTriangle { get; set; }
    internal CoverageEnclosureSite DominantSite { get; set; }
    internal int WorstVertex { get; set; }
    internal double DeviceTolerance { get; set; }
    internal int ActiveVertices { get; set; }
    internal int ActiveCommands { get; set; }
    internal ReadOnlySpan<V> HostModelBounds => Model.AsSpan(0, ActiveVertices);
    internal ReadOnlySpan<V> HostWorldBounds => World.AsSpan(0, ActiveVertices);

    internal void Reset(int commands)
    {
        ActiveVertices = Model.Length; ActiveCommands = commands;
        MayRefuse = false; DeviceTolerance = 0; DominantSite = CoverageEnclosureSite.None; WorstVertex = -1;
        FirstUnboundedSite = CoverageEnclosureSite.None; FirstUnboundedIndex = FirstUnboundedTriangle = -1;
        Array.Clear(Full); Array.Clear(Faces); Array.Clear(FaceGuards); Array.Clear(GroupSites);
        Array.Fill(Representatives, -1);
    }

    internal void Unbounded(CoverageEnclosureSite site, int index, int triangle = -1)
    {
        if (FirstUnboundedSite == CoverageEnclosureSite.None)
        { FirstUnboundedSite = site; FirstUnboundedIndex = index; FirstUnboundedTriangle = triangle; }
        Array.Fill(Model, CoverageEnclosureMath.Unbounded); Array.Fill(World, CoverageEnclosureMath.Unbounded);
        DeviceTolerance = double.PositiveInfinity;
    }
}
