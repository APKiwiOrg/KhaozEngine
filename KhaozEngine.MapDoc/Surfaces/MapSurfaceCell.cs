using System;

namespace KhaozEngine.MapDoc.Surfaces;

public enum MapOverlayCut : byte { Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter }
[Flags]
public enum MapCellFlags : byte { None = 0, Blocked = 1, Indoor = 2, LegacyBridge = 4, NoDraw = 8, FeatherOverlay = 16 }
public enum MapCellTopology : byte { Auto, ForceSwNe, ForceNwSe }
public readonly record struct MapSurfaceCell(ushort Underlay, ushort Overlay, MapOverlayCut Cut,
    byte Rotation, MapCellFlags Flags, MapCellTopology Topology);
public enum MapCellEdge : byte { South, East, North, West }
public sealed record MapVertexOwner(MapPatchKey Patch, MapLatticeAddress Address);
public sealed record MapCornerDependency(int CornerX, int CornerZ, MapVertexOwner Owner);
public sealed record MapEdgeSubdivision(int CellX, int CellZ, MapCellEdge Edge, int Segments);
