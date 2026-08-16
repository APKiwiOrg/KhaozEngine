using System.Collections.Generic;

namespace KhaozEngine.TileEdit;

/// <summary>Everything authored and derived at one tile: both material layers with their catalog names, the
/// overlay shape and rotation, the authored settings and the derived collision as flag-name lists, and the four
/// corner heights in centimetres (SW, SE, NW, NE). <see cref="Region"/> is the region coordinate that holds the
/// tile, or the literal <c>missing</c> when the world has no region there.</summary>
public sealed record TileInfo(int X, int Z, int Plane,
    ushort Underlay, string? UnderlayName, ushort Overlay, string? OverlayName,
    string Shape, int Rotation, string Settings, string Collision, bool Blocked,
    short[] CornersCm, string Region);

/// <summary>A one-character-per-tile map of a rect on one plane. <see cref="Rows"/> runs NORTH FIRST (the
/// highest z is row 0), so it reads the same way round as the top-down render, and each row runs west to east.
/// <see cref="Legend"/> spells out what the characters mean for this layer.</summary>
public sealed record TileMapResult(RectInfo Rect, int Plane, string Layer, IReadOnlyList<string> Rows, string Legend);

/// <summary>A rect of the corner-height lattice in centimetres. <see cref="Rows"/> runs north first like
/// <see cref="TileMapResult"/>, each row west to east.</summary>
public sealed record HeightMapResult(RectInfo CornerRect, int Plane, IReadOnlyList<short[]> Rows);

/// <summary>The derived collision at one tile: the flag names, whether the whole tile is impassable, and
/// whether a one-tile agent standing here could take each cardinal step.</summary>
public sealed record CollisionInfo(int X, int Z, int Plane, string Flags, bool Blocked,
    bool CanStepNorth, bool CanStepEast, bool CanStepSouth, bool CanStepWest);

/// <summary>Whether an agent of <see cref="AgentSize"/> tiles square anchored at this tile stands on ground it
/// could occupy, which needs every tile of that footprint to be unblocked.</summary>
public sealed record WalkableInfo(int X, int Z, int Plane, int AgentSize, bool Walkable, string Collision);

/// <summary>One step of a path.</summary>
public sealed record TileStep(int X, int Z);

/// <summary>A path result: whether the walk ended on the requested goal, the steps after the start, and how
/// many there are. An unreachable goal still returns the walk to the nearest reachable tile.</summary>
public sealed record PathResult(bool Reached, IReadOnlyList<TileStep> Steps, int Length);

/// <summary>One placed object: its id, archetype, anchor, plane, rotation, tags, and the tile rect its rotated
/// footprint covers.</summary>
public sealed record ObjectInfo(long Id, string ArchetypeId, int X, int Z, int Plane, int Rotation,
    IReadOnlyList<string> Tags, RectInfo Footprint);

/// <summary>One named marker: where it stands and what it is tagged with.</summary>
public sealed record MarkerInfo(string Name, int X, int Z, int Plane, IReadOnlyList<string> Tags);

/// <summary>One ground material of the loaded catalogs.</summary>
public sealed record MaterialInfo(ushort Id, string Name, string Color, string Kind);

/// <summary>One object archetype of the loaded catalogs, with the footprint and collision an authoring client
/// needs before it places one.</summary>
public sealed record ArchetypeInfo(string Id, string Name, string MeshRef, int SizeX, int SizeZ,
    string CollisionKind, bool IsRoof, bool Interactive, IReadOnlyList<string> Tags);

/// <summary>A catalog listing. Only the list matching the requested kind is filled, the other is empty, so a
/// client reads one shape whichever kind it asked for.</summary>
public sealed record CatalogListResult(string Kind, IReadOnlyList<MaterialInfo> Materials,
    IReadOnlyList<ArchetypeInfo> Archetypes);

/// <summary>One region the world holds: its coordinate, the tile rect it covers, and what is anchored in it.</summary>
public sealed record RegionInfo(int Rx, int Rz, RectInfo Rect, int ObjectCount, int MarkerCount);

/// <summary>One prefab file found in a directory listing.</summary>
public sealed record PrefabFileInfo(string Name, string Path, long SizeBytes);
