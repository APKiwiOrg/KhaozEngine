using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A document-unique record stored once in its anchor patch.</summary>
public abstract record MapTopologyRecord(string Id);
public readonly record struct MapLatticeVertex(string SurfaceId, MapLatticeAddress Address);
public sealed record MapSurfaceEdgeRef(MapPatchKey Patch, MapLatticeVertex From, MapLatticeVertex To);
public sealed record MapSurfaceSeam(string Id, MapSurfaceEdgeRef First, MapSurfaceEdgeRef Second,
    IReadOnlyList<(MapLatticeVertex First, MapLatticeVertex Second)> Pairs) : MapTopologyRecord(Id);
public enum MapChainKind : byte { SurfaceEdge, Authored }
public sealed record MapChainVertex(MapLatticeVertex Vertex, int? HeightUnits);
public sealed record MapBoundaryChain(string Id, MapChainKind Kind, MapPatchKey? SourcePatch,
    IReadOnlyList<MapChainVertex> Vertices) : MapTopologyRecord(Id);
public enum MapStripFacing : byte { Front, Back, TwoSided }
public sealed record MapWallStrip(string Id, MapRecordRef LowerChain, MapRecordRef UpperChain,
    MapStripFacing Facing, ushort MaterialId) : MapTopologyRecord(Id);
public sealed record MapCavePortal(string Id, MapRecordRef FromSpace, MapRecordRef ToSpace,
    IReadOnlyList<MapLatticeVertex> Interval, MapRecordRef BandBottom, MapRecordRef? BandTop) : MapTopologyRecord(Id);
public sealed record MapHorizontalOpening(string Id, MapPatchKey Patch,
    IReadOnlyList<int> SlotCells) : MapTopologyRecord(Id);
public sealed record MapVerticalLink(string Id, MapRecordRef UpperSpace, MapRecordRef LowerSpace,
    IReadOnlyList<MapRecordRef> Openings, IReadOnlyList<MapRecordRef> Portals,
    IReadOnlyList<MapRecordRef> GeometryOwners) : MapTopologyRecord(Id);
