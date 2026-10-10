using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

public enum MapSpaceKind : byte { Cave, Exterior }
public enum MapSide : byte { Front, Back }
public sealed record MapBoundaryRef(MapRecordRef Record, MapSide Side);
public sealed record MapSpaceDoc(string Id, MapSpaceKind Kind, MapRecordRef? Parent, MapRecordRef? AliasOf,
    IReadOnlyList<string> DomainTags, IReadOnlyList<MapBoundaryRef> Walls,
    IReadOnlyList<MapBoundaryRef> Portals, IReadOnlyList<MapRecordRef> Links) : MapTopologyRecord(Id);
public enum MapBoundKind : byte { SupportFloor, Ceiling, HorizontalOpening, OpenTop, LegacyExteriorV1 }
public sealed record MapBoundRef(MapBoundKind Kind, string? SurfaceId, MapRecordRef? Opening);
public sealed record MapSpaceFootprint(string Id, MapRecordRef Space, MapPatchKey Lattice,
    IReadOnlyList<int> SlotCells, MapBoundRef Lower, MapBoundRef Upper) : MapTopologyRecord(Id);
