using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>Read-only queries over the session's open world: one tile in full, ASCII maps of a rect on any
/// layer, the corner-height lattice, derived collision and walkability, a path, and the content lists (objects,
/// markers, catalogs, regions, prefab files). Everything reads through <see cref="TileEditSession.Read{T}"/>, so
/// a query sees one consistent world even while another call is mid-edit. Nothing here mutates.
///
/// <para>Every map runs NORTH FIRST: row 0 is the highest z of the rect, so an ASCII map and a top-down render
/// of the same rect read the same way round, and each row runs west to east.</para></summary>
public sealed partial class QueryService(TileEditSession session)
{
    /// <summary>The layer names <see cref="TilesGetRect"/> accepts.</summary>
    public const string LayerNames = "underlay, overlay, shape, settings, collision";

    // One character per id, so a map stays one column per tile. Ids above 35 wrap, which a legend says outright:
    // the map is for reading shape and structure, and tile_get is there for the exact id under any character.
    const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Everything authored and derived at one tile, with both material ids resolved to their catalog
    /// names and the settings and collision flags spelled out.</summary>
    public TileInfo TileGet(int x, int z, int plane) => session.Read(e =>
    {
        TileWorldDocument d = e.Document;
        ushort underlay = d.GetUnderlay(x, z, plane);
        ushort overlay = d.GetOverlay(x, z, plane);
        TileCollisionFlags flags = e.Collision.Get(x, z, plane);
        short[] corners =
        {
            d.CornerHeightCm(x, z, plane), d.CornerHeightCm(x + 1, z, plane),
            d.CornerHeightCm(x, z + 1, plane), d.CornerHeightCm(x + 1, z + 1, plane),
        };
        return new TileInfo(x, z, plane,
            underlay, e.Catalogs.Material(underlay)?.Name,
            overlay, e.Catalogs.Material(overlay)?.Name,
            d.GetOverlayShape(x, z, plane).ToString(), d.GetOverlayRotation(x, z, plane),
            SettingNames(d.GetSettings(x, z, plane)), CollisionNames(flags),
            (flags & TileCollisionFlags.Blocked) != 0, corners,
            d.RegionAt(x, z) is null ? "missing" : RegionCoord.Of(x, z).ToString());
    });

    /// <summary>A one-character-per-tile map of <paramref name="layer"/> over the rect on one plane, north
    /// first, with the legend that decodes it.</summary>
    /// <exception cref="ArgumentException">The rect covers no tiles, or the layer is not one of
    /// <see cref="LayerNames"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The plane is outside the world's range.</exception>
    public TileMapResult TilesGetRect(TileRect rect, int plane, string layer)
    {
        RequireRect(rect);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);
        string key = layer.Trim().ToLowerInvariant();
        Func<TileEditingDocument, int, int, int, char> pick;
        string legend;
        switch (key)
        {
            case "underlay":
                pick = Underlay;
                legend = "one base36 digit per tile of the underlay material id modulo 36, '.' for void (id 0). Ids above 35 wrap, tile_get names the exact id.";
                break;
            case "overlay":
                pick = Overlay;
                legend = "one base36 digit per tile of the overlay material id modulo 36, '.' for none (id 0). Ids above 35 wrap, tile_get names the exact id.";
                break;
            case "shape":
                pick = Shape;
                legend = "'.' full tile, 'd' diagonal half, 'q' corner quarter, 't' corner three quarter.";
                break;
            case "settings":
                pick = Settings;
                legend = "'.' none, 'b' blocked, 'i' indoors, 'r' bridge, 'x' nodraw, 'B' blocked and nodraw, '+' another mix (tile_get names the flags).";
                break;
            case "collision":
                pick = Collision;
                legend = "'#' blocked, '|' a west or east wall, '-' a north or south wall, '+' both, '.' open, 'v' no region.";
                // The other layers read through the document, which throws for itself on a plane it does not
                // have. This one reads the collision map, which answers Blocked for an unknown plane, so a
                // typo'd plane would come back as a map of solid rock rather than as an error.
                RequirePlane(plane);
                break;
            default:
                throw new ArgumentException($"'{layer}' is not a tile layer. The layers are {LayerNames}.", nameof(layer));
        }
        return session.Read(e => new TileMapResult(RectInfo.Of(rect), plane, key, Rows(e, rect, plane, pick), legend));
    }

    /// <summary>The corner-height lattice over a rect of CORNERS (not tiles) on one plane, in centimetres,
    /// NORTH FIRST: row 0 is the highest z of the rect, each row west to east. That is the same shape
    /// <c>MutationService.HeightsSet</c> takes, so a read, an edit and a write back round trip without
    /// flipping the terrain.</summary>
    /// <exception cref="ArgumentException">The rect covers no corners.</exception>
    public HeightMapResult HeightGetRect(TileRect cornerRect, int plane)
    {
        RequireRect(cornerRect);
        return session.Read(e =>
        {
            var rows = new List<short[]>(cornerRect.Height);
            for (int z = cornerRect.Z1 - 1; z >= cornerRect.Z; z--)
            {
                var row = new short[cornerRect.Width];
                for (int x = cornerRect.X; x < cornerRect.X1; x++)
                    row[x - cornerRect.X] = e.Document.CornerHeightCm(x, z, plane);
                rows.Add(row);
            }
            return new HeightMapResult(RectInfo.Of(cornerRect), plane, rows);
        });
    }

    /// <summary>The derived collision at one tile, with the four cardinal steps a one-tile agent standing there
    /// could take.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The plane is outside the world's range.</exception>
    public CollisionInfo CollisionAt(int x, int z, int plane)
    {
        RequirePlane(plane);
        return session.Read(e =>
        {
            TileCollisionMap map = e.Collision;
            return new CollisionInfo(x, z, plane, CollisionNames(map.Get(x, z, plane)),
                TileCollision.IsBlocked(map, x, z, plane),
                TileCollision.CanStep(map, x, z, plane, TileDirection.N),
                TileCollision.CanStep(map, x, z, plane, TileDirection.E),
                TileCollision.CanStep(map, x, z, plane, TileDirection.S),
                TileCollision.CanStep(map, x, z, plane, TileDirection.W));
        });
    }

    /// <summary>Whether an agent <paramref name="agentSize"/> tiles square anchored at this tile stands clear:
    /// every tile of that footprint must be unblocked, which is the same footprint rule the pathfinder walks
    /// with.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The plane is outside the world's range, or the agent is
    /// smaller than one tile.</exception>
    public WalkableInfo IsWalkable(int x, int z, int plane, int agentSize = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(agentSize, 1);
        RequirePlane(plane);
        return session.Read(e =>
        {
            bool walkable = true;
            for (int dz = 0; dz < agentSize && walkable; dz++)
                for (int dx = 0; dx < agentSize && walkable; dx++)
                    if (TileCollision.IsBlocked(e.Collision, x + dx, z + dz, plane)) walkable = false;
            return new WalkableInfo(x, z, plane, agentSize, walkable, CollisionNames(e.Collision.Get(x, z, plane)));
        });
    }

    /// <summary>The walk from one tile to another on one plane. An unreachable goal still returns the steps to
    /// the nearest reachable tile, with <see cref="PathResult.Reached"/> false, which includes a goal that is
    /// simply outside <paramref name="maxRadius"/> of the start.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The plane is outside the world's range, or the search
    /// radius is outside the pathfinder's own limits.</exception>
    public PathResult Path(int fromX, int fromZ, int toX, int toZ, int plane, int agentSize = 1,
        int maxRadius = TilePathfinder.DefaultMaxRadius)
    {
        RequirePlane(plane);
        return session.Read(e =>
        {
            TilePath path = TilePathfinder.FindPath(e.Collision, plane,
                new TileCoord(fromX, fromZ, plane), new TileCoord(toX, toZ, plane), agentSize, maxRadius);
            TileStep[] steps = path.Tiles.Select(t => new TileStep(t.X, t.Z)).ToArray();
            return new PathResult(path.Reached, steps, steps.Length);
        });
    }

    /// <summary>An ASCII map of what a one-tile agent could stand on over the rect, north first.</summary>
    /// <exception cref="ArgumentException">The rect covers no tiles.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The plane is outside the world's range.</exception>
    public TileMapResult WalkableRect(TileRect rect, int plane)
    {
        RequireRect(rect);
        RequirePlane(plane);
        return session.Read(e => new TileMapResult(RectInfo.Of(rect), plane, "walkable",
            Rows(e, rect, plane, (d, x, z, p) => TileCollision.IsBlocked(d.Collision, x, z, p) ? '#' : '.'),
            "'#' blocked, '.' open."));
    }

    /// <summary>The flag names of a derived collision value, comma separated, or <c>None</c>.</summary>
    public static string CollisionNames(TileCollisionFlags flags) =>
        Names(flags, TileCollisionFlags.None, v => (flags & v) == v);

    /// <summary>The flag names of an authored settings value, comma separated, or <c>None</c>.</summary>
    public static string SettingNames(TileSettings settings) =>
        Names(settings, TileSettings.None, v => (settings & v) == v);

    // The rows of a map, north first (row 0 is the highest z) and west to east within a row, which is the one
    // place that orientation is decided for every layer.
    static IReadOnlyList<string> Rows(TileEditingDocument e, TileRect rect, int plane,
        Func<TileEditingDocument, int, int, int, char> pick)
    {
        var rows = new List<string>(rect.Height);
        var row = new char[rect.Width];
        for (int z = rect.Z1 - 1; z >= rect.Z; z--)
        {
            for (int x = rect.X; x < rect.X1; x++) row[x - rect.X] = pick(e, x, z, plane);
            rows.Add(new string(row));
        }
        return rows;
    }

    static char Underlay(TileEditingDocument e, int x, int z, int plane) => Digit(e.Document.GetUnderlay(x, z, plane));

    static char Overlay(TileEditingDocument e, int x, int z, int plane) => Digit(e.Document.GetOverlay(x, z, plane));

    static char Digit(ushort id) => id == 0 ? '.' : Base36[id % 36];

    static char Shape(TileEditingDocument e, int x, int z, int plane) => e.Document.GetOverlayShape(x, z, plane) switch
    {
        TileOverlayShape.DiagonalHalf => 'd',
        TileOverlayShape.CornerQuarter => 'q',
        TileOverlayShape.CornerThreeQuarter => 't',
        _ => '.',
    };

    // Named characters for the combinations an author reads for, and '+' for the rest. A per-combination
    // alphabet would need sixteen characters and a legend nobody could hold in their head, and tile_get already
    // gives the exact flag names for any tile the map made someone curious about.
    static char Settings(TileEditingDocument e, int x, int z, int plane) => e.Document.GetSettings(x, z, plane) switch
    {
        TileSettings.None => '.',
        TileSettings.Blocked => 'b',
        TileSettings.Indoors => 'i',
        TileSettings.Bridge => 'r',
        TileSettings.NoDraw => 'x',
        TileSettings.Blocked | TileSettings.NoDraw => 'B',
        _ => '+',
    };

    // A region the world does not hold reads 'v' rather than '#': the collision map answers blocked for it, and
    // an author looking at a map needs to tell "there is a wall here" from "there is no world here".
    static char Collision(TileEditingDocument e, int x, int z, int plane)
    {
        if (e.Document.RegionAt(x, z) is null) return 'v';
        TileCollisionFlags f = e.Collision.Get(x, z, plane);
        if ((f & TileCollisionFlags.Blocked) != 0) return '#';
        bool sideWall = (f & (TileCollisionFlags.WallW | TileCollisionFlags.WallE)) != 0;
        bool endWall = (f & (TileCollisionFlags.WallN | TileCollisionFlags.WallS)) != 0;
        if (sideWall && endWall) return '+';
        if (sideWall) return '|';
        return endWall ? '-' : '.';
    }

    static string Names<T>(T value, T none, Func<T, bool> has) where T : struct, Enum
    {
        if (value.Equals(none)) return none.ToString()!;
        var parts = new List<string>();
        foreach (T v in Enum.GetValues<T>())
            if (!v.Equals(none) && has(v)) parts.Add(v.ToString()!);
        return string.Join(",", parts);
    }

    static void RequireRect(TileRect rect)
    {
        if (rect.IsEmpty)
            throw new ArgumentException(
                $"the rect ({rect.X}, {rect.Z}, {rect.Width}, {rect.Height}) covers nothing.", nameof(rect));
    }

    // Every query that reads the COLLISION MAP checks the plane through here first. The map answers Blocked for
    // a plane it does not have, by design (an unloaded region has to read as a wall rather than as a void), so a
    // query handed a plane the world does not have would come back as a plausible map of solid rock instead of
    // an error. Every other layer reads through the document, which throws for itself. A closed session throws
    // TileWorldException from Read before the range is ever considered, which is the right precedence: no world
    // open is the more fundamental complaint.
    void RequirePlane(int plane)
    {
        int planes = session.Read(e => e.Document.PlaneCount);
        if ((uint)plane >= (uint)planes)
            throw new ArgumentOutOfRangeException(nameof(plane), plane,
                $"the world has {planes} planes, so the plane must be 0..{planes - 1}.");
    }
}
