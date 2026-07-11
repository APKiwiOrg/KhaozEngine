using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Dungeon.Internal;

/// <summary>Output of one <see cref="RoomGrower.Grow"/> pass: the filled (pre-wall-pass) cell raster plus the
/// room and edge lists carved on it, and whether the grower stopped short of the requested room count.</summary>
internal sealed record GrowResult(
    DungeonCellKind[] Cells,
    List<DungeonRoom> Rooms,
    List<DungeonEdge> Edges,
    bool Saturated);

/// <summary>
/// Room growth for <c>DungeonGenerator</c>. Stateless: <see cref="Grow"/> owns all mutable state locally so
/// concurrent generations never interfere. Places an entrance room centered on floor 0, then grows a tree of
/// rooms joined by straight axis-aligned corridors and, when the config allows more than one floor, by upward
/// stair runs. Each room is committed together with its connection (corridor or stair), its door frames, and its
/// edge atomically, validated whole before any cell is written. Growth is upward only: a stair carries a room
/// from floor F to floor F+1, the entrance stays on floor 0, and no stair ever descends.
/// </summary>
internal static class RoomGrower
{
    // North, East, South, West as (dx, dz) unit steps.
    private static readonly (int Dx, int Dz)[] Directions =
    {
        (0, -1),
        (1, 0),
        (0, 1),
        (-1, 0),
    };

    /// <summary>Grows the layout from the floor-0 entrance using the given RNG stream, spreading across floors via
    /// stairs when <see cref="DungeonConfig.MaxFloors"/> exceeds 1, and returning the raster and room graph before
    /// the wall pass. Deterministic in <paramref name="config"/> and the <paramref name="rooms"/> stream.</summary>
    internal static GrowResult Grow(DungeonConfig config, DeterministicRng rooms)
    {
        int width = config.PlotWidthTiles;
        int depth = config.PlotDepthTiles;
        int floors = config.MaxFloors;
        var grid = new Grid(width, depth, floors);

        var roomList = new List<DungeonRoom>();
        var edgeList = new List<DungeonEdge>();

        int entranceWidth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
        int entranceDepth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
        var entrance = new DungeonRoom
        {
            Id = 0,
            Floor = 0,
            X = (width - entranceWidth) / 2,
            Z = (depth - entranceDepth) / 2,
            Width = entranceWidth,
            Depth = entranceDepth,
            RoomType = DungeonRoomType.Entrance,
        };
        WriteRoom(grid, entrance);
        roomList.Add(entrance);

        int target = config.RoomCountTarget;
        int attemptCap = target * 64;
        int attempts = 0;
        var saturated = new HashSet<int>();

        while (roomList.Count < target && attempts < attemptCap)
        {
            var open = new List<DungeonRoom>();
            foreach (DungeonRoom room in roomList)
            {
                if (!saturated.Contains(room.Id))
                {
                    open.Add(room);
                }
            }

            if (open.Count == 0)
            {
                break;
            }

            attempts++;
            DungeonRoom frontier = open[rooms.Next(open.Count)];
            if (!TryGrow(config, rooms, grid, frontier, roomList, edgeList))
            {
                saturated.Add(frontier.Id);
            }
        }

        bool wasSaturated = roomList.Count < target;
        return new GrowResult(grid.Cells, roomList, edgeList, wasSaturated);
    }

    /// <summary>Turns every <see cref="DungeonCellKind.Empty"/> cell that is 8-adjacent (same floor) to a
    /// walkable cell into a <see cref="DungeonCellKind.Wall"/>. Runs once after all placement so no walkable
    /// cell is left 8-adjacent to empty. Non-empty cells (including <see cref="DungeonCellKind.StairVoid"/>)
    /// are untouched.</summary>
    internal static void ApplyWallPass(DungeonCellKind[] cells, int width, int depth, int floors)
    {
        var toWall = new List<int>();
        for (int f = 0; f < floors; f++)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (f * depth + z) * width + x;
                    if (cells[idx] != DungeonCellKind.Empty)
                    {
                        continue;
                    }

                    if (HasWalkableNeighbor(cells, width, depth, x, z, f))
                    {
                        toWall.Add(idx);
                    }
                }
            }
        }

        foreach (int idx in toWall)
        {
            cells[idx] = DungeonCellKind.Wall;
        }
    }

    private static bool HasWalkableNeighbor(DungeonCellKind[] cells, int width, int depth, int x, int z, int f)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }

                int nx = x + dx;
                int nz = z + dz;
                if (nx < 0 || nx >= width || nz < 0 || nz >= depth)
                {
                    continue;
                }

                if (DungeonLayout.IsWalkable(cells[(f * depth + nz) * width + nx]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGrow(
        DungeonConfig config,
        DeterministicRng rooms,
        Grid grid,
        DungeonRoom source,
        List<DungeonRoom> roomList,
        List<DungeonEdge> edgeList)
    {
        var dirs = new (int Dx, int Dz)[Directions.Length];
        Array.Copy(Directions, dirs, Directions.Length);
        for (int i = dirs.Length - 1; i > 0; i--)
        {
            int j = rooms.Next(i + 1);
            (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
        }

        foreach ((int Dx, int Dz) dir in dirs)
        {
            bool horizontal = dir.Dz == 0;
            int lateralSpan = horizontal ? source.Depth : source.Width;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                // Per-candidate stair draw, taken first so the corridor branch keeps Task 3's exact draw order.
                // The floor check short-circuits before the draw, so a single-floor config (or a room already on
                // the top floor) never consumes a stair draw and grows byte-for-byte identically to before stairs.
                bool tryStair = source.Floor < config.MaxFloors - 1 && rooms.Next(4) == 0;
                if (tryStair)
                {
                    int stairWidth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
                    int stairDepth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
                    int stairLateral = rooms.Next(lateralSpan);

                    StairCandidate stair = BuildStairCandidate(source, dir, stairWidth, stairDepth, stairLateral);
                    if (ValidateStair(grid, source, stair))
                    {
                        CommitStair(grid, source, stair, roomList, edgeList);
                        return true;
                    }

                    continue;
                }

                int length = rooms.Next(1, 5);

                // A hall is an elongated room whose long axis runs along the corridor that reaches it. The hall
                // decision draw is guarded like the width/stair draws, so a HallChancePercent==0 config consumes
                // nothing and takes the normal two room-dimension draws in the original order (byte-compat).
                bool isHall = DrawIsHall(config, rooms);
                int newWidth;
                int newDepth;
                if (isHall)
                {
                    int hallLength = rooms.Next(config.HallMinLengthTiles, config.HallMaxLengthTiles + 1);
                    int hallGirth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
                    if (horizontal)
                    {
                        newWidth = hallLength;
                        newDepth = hallGirth;
                    }
                    else
                    {
                        newWidth = hallGirth;
                        newDepth = hallLength;
                    }
                }
                else
                {
                    newWidth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
                    newDepth = rooms.Next(config.RoomMinTiles, config.RoomMaxTiles + 1);
                }

                // The corridor width is drawn (and only drawn) when the config opens a range, guarded exactly like
                // the stair/loop draws so a min==max==1 config consumes nothing and grows byte-for-byte as before.
                // It is then capped to the narrower of the source edge (lateralSpan) and the new room's facing edge
                // (perpNewRoom) so the door band fits both, and the lateral start is bounded so the whole band lies
                // on the source edge. For width 1 that bound is rooms.Next(lateralSpan) - the identical legacy draw.
                int corridorWidth = DrawCorridorWidth(config, rooms);
                int perpNewRoom = horizontal ? newDepth : newWidth;
                int w = Math.Min(corridorWidth, Math.Min(lateralSpan, perpNewRoom));
                int lateral = rooms.Next(lateralSpan - w + 1);

                Candidate candidate = BuildCandidate(source, dir, length, newWidth, newDepth, lateral, w);
                if (Validate(grid, source, candidate))
                {
                    Commit(grid, source, candidate, isHall, roomList, edgeList);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Draws a corridor width from <paramref name="rooms"/> only when the config opens a range
    /// (<see cref="DungeonConfig.CorridorMaxWidth"/> &gt; <see cref="DungeonConfig.CorridorMinWidth"/>); a fixed
    /// width consumes nothing, so a default 1/1 config draws nothing and stays byte-identical. Same guard
    /// discipline as the stair and loop-edge draws.</summary>
    internal static int DrawCorridorWidth(DungeonConfig config, DeterministicRng rooms)
    {
        if (config.CorridorMaxWidth > config.CorridorMinWidth)
        {
            return rooms.Next(config.CorridorMinWidth, config.CorridorMaxWidth + 1);
        }

        return config.CorridorMinWidth;
    }

    /// <summary>Draws the per-attempt hall decision from <paramref name="rooms"/> only when
    /// <see cref="DungeonConfig.HallChancePercent"/> is positive; a zero chance consumes nothing, so a halls-off
    /// config draws nothing and stays byte-identical. Same guard discipline as the corridor-width draw.</summary>
    private static bool DrawIsHall(DungeonConfig config, DeterministicRng rooms)
    {
        if (config.HallChancePercent <= 0)
        {
            return false;
        }

        return rooms.Next(100) < config.HallChancePercent;
    }

    /// <summary>Builds a <paramref name="corridorWidth"/>-wide corridor candidate: a straight rectangular tube of
    /// door + corridor cells (constant perpendicular band from source edge to new room edge) plus the new room
    /// rect. The band spans <paramref name="corridorWidth"/> cells perpendicular to the march direction, starting
    /// at the source edge offset <paramref name="lateral"/>. The new room is centered so the band lies fully on
    /// its facing edge; for width 1 it uses the legacy single-line centering, so a width-1 candidate is identical
    /// to the pre-width geometry (cells, order, and room position).</summary>
    private static Candidate BuildCandidate(
        DungeonRoom source,
        (int Dx, int Dz) dir,
        int length,
        int newWidth,
        int newDepth,
        int lateral,
        int corridorWidth)
    {
        int floor = source.Floor;
        int sx0 = source.X;
        int sx1 = source.X + source.Width - 1;
        int sz0 = source.Z;
        int sz1 = source.Z + source.Depth - 1;

        var doorSrc = new DungeonTile[corridorWidth];
        var doorNew = new DungeonTile[corridorWidth];
        var corridor = new DungeonTile[corridorWidth * length];

        int roomX;
        int roomZ;

        if (dir.Dz == 0)
        {
            // Horizontal march: the band spans Z; the source edge column and door column are fixed in X.
            int lineX = dir.Dx > 0 ? sx1 : sx0;
            int doorX = lineX + dir.Dx;
            int bandZ0 = sz0 + lateral;

            for (int p = 0; p < corridorWidth; p++)
            {
                int z = bandZ0 + p;
                doorSrc[p] = new DungeonTile(doorX, z, floor);
                for (int k = 0; k < length; k++)
                {
                    corridor[k * corridorWidth + p] = new DungeonTile(doorX + dir.Dx * (k + 1), z, floor);
                }

                doorNew[p] = new DungeonTile(doorX + dir.Dx * (length + 1), z, floor);
            }

            int nearX = doorNew[0].X + dir.Dx;
            roomX = dir.Dx > 0 ? nearX : nearX - (newWidth - 1);
            roomZ = corridorWidth == 1 ? bandZ0 - newDepth / 2 : bandZ0 - (newDepth - corridorWidth) / 2;
        }
        else
        {
            // Vertical march: the band spans X; the source edge row and door row are fixed in Z.
            int lineZ = dir.Dz > 0 ? sz1 : sz0;
            int doorZ = lineZ + dir.Dz;
            int bandX0 = sx0 + lateral;

            for (int p = 0; p < corridorWidth; p++)
            {
                int x = bandX0 + p;
                doorSrc[p] = new DungeonTile(x, doorZ, floor);
                for (int k = 0; k < length; k++)
                {
                    corridor[k * corridorWidth + p] = new DungeonTile(x, doorZ + dir.Dz * (k + 1), floor);
                }

                doorNew[p] = new DungeonTile(x, doorZ + dir.Dz * (length + 1), floor);
            }

            int nearZ = doorNew[0].Z + dir.Dz;
            roomZ = dir.Dz > 0 ? nearZ : nearZ - (newDepth - 1);
            roomX = corridorWidth == 1 ? bandX0 - newWidth / 2 : bandX0 - (newWidth - corridorWidth) / 2;
        }

        return new Candidate(floor, roomX, roomZ, newWidth, newDepth, doorSrc, doorNew, corridor);
    }

    private static bool Validate(Grid grid, DungeonRoom source, Candidate candidate)
    {
        // New room interior: one tile clear of the plot border (so its ring is in-plot) and currently empty.
        for (int x = candidate.RoomX; x < candidate.RoomX + candidate.RoomWidth; x++)
        {
            for (int z = candidate.RoomZ; z < candidate.RoomZ + candidate.RoomDepth; z++)
            {
                if (!grid.InPlotWithMargin(x, z))
                {
                    return false;
                }

                if (grid.Get(x, z, candidate.Floor) != DungeonCellKind.Empty)
                {
                    return false;
                }
            }
        }

        // New room's 1-cell margin ring: in-plot and currently empty (the wall pass fills it later; the new
        // door frame sits on it and is empty until committed).
        for (int x = candidate.RoomX - 1; x <= candidate.RoomX + candidate.RoomWidth; x++)
        {
            for (int z = candidate.RoomZ - 1; z <= candidate.RoomZ + candidate.RoomDepth; z++)
            {
                bool interior = x >= candidate.RoomX && x < candidate.RoomX + candidate.RoomWidth
                    && z >= candidate.RoomZ && z < candidate.RoomZ + candidate.RoomDepth;
                if (interior)
                {
                    continue;
                }

                if (!grid.InBounds(x, z))
                {
                    return false;
                }

                if (grid.Get(x, z, candidate.Floor) != DungeonCellKind.Empty)
                {
                    return false;
                }
            }
        }

        // Every door-band and corridor-band cell: clear of the border and currently empty. The band cells are
        // unwritten during validation, so they never count each other as occupied (a wide tube validates whole).
        foreach (DungeonTile tile in candidate.DoorSrc)
        {
            if (!IsClearWalkableCell(grid, tile, candidate.Floor))
            {
                return false;
            }
        }

        foreach (DungeonTile tile in candidate.DoorNew)
        {
            if (!IsClearWalkableCell(grid, tile, candidate.Floor))
            {
                return false;
            }
        }

        foreach (DungeonTile tile in candidate.Corridor)
        {
            if (!IsClearWalkableCell(grid, tile, candidate.Floor))
            {
                return false;
            }
        }

        // No door-band or corridor-band cell may be orthogonally adjacent to any existing walkable cell other
        // than the source room interior they join (their own band and the new interior are not written yet, so
        // the band's own cells read as empty here and only foreign structure trips the check).
        HashSet<(int X, int Z)> allowed = RoomInterior(source);
        foreach (DungeonTile tile in candidate.DoorSrc)
        {
            if (HasForeignOrthogonalWalkable(grid, tile, candidate.Floor, allowed))
            {
                return false;
            }
        }

        foreach (DungeonTile tile in candidate.DoorNew)
        {
            if (HasForeignOrthogonalWalkable(grid, tile, candidate.Floor, allowed))
            {
                return false;
            }
        }

        foreach (DungeonTile tile in candidate.Corridor)
        {
            if (HasForeignOrthogonalWalkable(grid, tile, candidate.Floor, allowed))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="tile"/> is in-plot (one tile clear of the border, so its own ring
    /// stays in-plot) and currently <see cref="DungeonCellKind.Empty"/> on <paramref name="floor"/>. Shared by
    /// growth corridor/door validation and <c>LoopPlanner</c>'s loop-edge corridor validation.</summary>
    internal static bool IsClearWalkableCell(Grid grid, DungeonTile tile, int floor)
    {
        if (!grid.InPlotWithMargin(tile.X, tile.Z))
        {
            return false;
        }

        return grid.Get(tile.X, tile.Z, floor) == DungeonCellKind.Empty;
    }

    /// <summary>True when <paramref name="tile"/> is orthogonally adjacent, on <paramref name="floor"/>, to a
    /// walkable cell that is not in <paramref name="allowed"/> (the room interiors the corridor is meant to
    /// join). Shared by growth corridor/door validation and <c>LoopPlanner</c>'s loop-edge corridor
    /// validation.</summary>
    internal static bool HasForeignOrthogonalWalkable(Grid grid, DungeonTile tile, int floor, HashSet<(int X, int Z)> allowed)
    {
        Span<(int Dx, int Dz)> steps = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        foreach ((int dx, int dz) in steps)
        {
            int nx = tile.X + dx;
            int nz = tile.Z + dz;
            if (!grid.InBounds(nx, nz))
            {
                continue;
            }

            if (DungeonLayout.IsWalkable(grid.Get(nx, nz, floor)) && !allowed.Contains((nx, nz)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The set of interior tile coordinates <paramref name="room"/> occupies, used as the
    /// "allowed" set for <see cref="HasForeignOrthogonalWalkable"/> so a corridor endpoint's own room(s) don't
    /// count as foreign. Shared by growth corridor/door validation and <c>LoopPlanner</c>'s loop-edge corridor
    /// validation (which unions the sets of both rooms an existing-to-existing loop edge joins).</summary>
    internal static HashSet<(int X, int Z)> RoomInterior(DungeonRoom room)
    {
        var set = new HashSet<(int X, int Z)>();
        for (int x = room.X; x < room.X + room.Width; x++)
        {
            for (int z = room.Z; z < room.Z + room.Depth; z++)
            {
                set.Add((x, z));
            }
        }

        return set;
    }

    private static void Commit(
        Grid grid,
        DungeonRoom source,
        Candidate candidate,
        bool isHall,
        List<DungeonRoom> roomList,
        List<DungeonEdge> edgeList)
    {
        int newId = roomList.Count;
        var room = new DungeonRoom
        {
            Id = newId,
            Floor = candidate.Floor,
            X = candidate.RoomX,
            Z = candidate.RoomZ,
            Width = candidate.RoomWidth,
            Depth = candidate.RoomDepth,
            RoomType = isHall ? DungeonRoomType.Hall : DungeonRoomType.Normal,
        };
        WriteRoom(grid, room);

        foreach (DungeonTile tile in candidate.Corridor)
        {
            grid.Set(tile.X, tile.Z, tile.Floor, DungeonCellKind.Corridor);
        }

        foreach (DungeonTile tile in candidate.DoorSrc)
        {
            grid.Set(tile.X, tile.Z, tile.Floor, DungeonCellKind.DoorFrame);
        }

        foreach (DungeonTile tile in candidate.DoorNew)
        {
            grid.Set(tile.X, tile.Z, tile.Floor, DungeonCellKind.DoorFrame);
        }

        // Doors list is the source-end band followed by the new-end band (for width 1 this is exactly the legacy
        // [doorSrc, doorNew] pair, so the edge hashes identically).
        var doors = new DungeonTile[candidate.DoorSrc.Length + candidate.DoorNew.Length];
        candidate.DoorSrc.CopyTo(doors, 0);
        candidate.DoorNew.CopyTo(doors, candidate.DoorSrc.Length);

        roomList.Add(room);
        edgeList.Add(new DungeonEdge
        {
            RoomA = source.Id,
            RoomB = newId,
            Kind = DungeonEdgeKind.Corridor,
            Path = candidate.Corridor,
            Doors = doors,
        });
    }

    private static StairCandidate BuildStairCandidate(
        DungeonRoom source,
        (int Dx, int Dz) dir,
        int newWidth,
        int newDepth,
        int lateral)
    {
        int floor = source.Floor;
        int upper = floor + 1;
        int sx0 = source.X;
        int sx1 = source.X + source.Width - 1;
        int sz0 = source.Z;
        int sz1 = source.Z + source.Depth - 1;

        // The source interior cell the door frame attaches to, on the chosen line.
        int lineX;
        int lineZ;
        if (dir.Dz == 0)
        {
            lineZ = sz0 + lateral;
            lineX = dir.Dx > 0 ? sx1 : sx0;
        }
        else
        {
            lineX = sx0 + lateral;
            lineZ = dir.Dz > 0 ? sz1 : sz0;
        }

        // doorA -> StairLower -> StairMid -> StairUpper march straight out on floor F: a three-tread run that
        // climbs the whole floor at a walkable ~34-degree pitch (atan(floorHeight / 3*cellSize)), well under the
        // default 45-degree max slope. The run is capped on floor F+1 by an OPEN shaft - a StairVoid directly
        // above every tread, the headroom the ramp climbs through - and the landing StairTop sits one cell PAST
        // the top tread at the shaft's upper edge (StairTop IS room B's ring door). So a climber ascends through
        // the open shaft and steps onto solid landing floor, instead of wedging its head under a landing slab
        // parked over the top tread.
        var doorA = new DungeonTile(lineX + dir.Dx, lineZ + dir.Dz, floor);
        var stairLower = new DungeonTile(doorA.X + dir.Dx, doorA.Z + dir.Dz, floor);
        var stairMid = new DungeonTile(stairLower.X + dir.Dx, stairLower.Z + dir.Dz, floor);
        var stairUpper = new DungeonTile(stairMid.X + dir.Dx, stairMid.Z + dir.Dz, floor);
        var stairTop = new DungeonTile(stairUpper.X + dir.Dx, stairUpper.Z + dir.Dz, upper);
        var voidLower = new DungeonTile(stairLower.X, stairLower.Z, upper);
        var voidMid = new DungeonTile(stairMid.X, stairMid.Z, upper);
        var voidUpper = new DungeonTile(stairUpper.X, stairUpper.Z, upper);

        // Room B on floor F+1 extends one step further out from its door (StairTop), on the same line.
        int nearX = stairTop.X + dir.Dx;
        int nearZ = stairTop.Z + dir.Dz;

        int roomX;
        int roomZ;
        if (dir.Dz == 0)
        {
            roomX = dir.Dx > 0 ? nearX : nearX - (newWidth - 1);
            roomZ = lineZ - newDepth / 2;
        }
        else
        {
            roomZ = dir.Dz > 0 ? nearZ : nearZ - (newDepth - 1);
            roomX = lineX - newWidth / 2;
        }

        return new StairCandidate(
            floor, roomX, roomZ, newWidth, newDepth,
            doorA, stairLower, stairMid, stairUpper, stairTop, voidLower, voidMid, voidUpper);
    }

    private static bool ValidateStair(Grid grid, DungeonRoom source, StairCandidate candidate)
    {
        int lower = candidate.Floor;
        int upper = candidate.Floor + 1;

        // Room B interior (floor F+1): one tile clear of the plot border and currently empty.
        for (int x = candidate.RoomX; x < candidate.RoomX + candidate.RoomWidth; x++)
        {
            for (int z = candidate.RoomZ; z < candidate.RoomZ + candidate.RoomDepth; z++)
            {
                if (!grid.InPlotWithMargin(x, z))
                {
                    return false;
                }

                if (grid.Get(x, z, upper) != DungeonCellKind.Empty)
                {
                    return false;
                }
            }
        }

        // Room B's 1-cell margin ring (floor F+1): in-plot and currently empty. StairTop, the new door, sits on
        // this ring and is empty until committed.
        for (int x = candidate.RoomX - 1; x <= candidate.RoomX + candidate.RoomWidth; x++)
        {
            for (int z = candidate.RoomZ - 1; z <= candidate.RoomZ + candidate.RoomDepth; z++)
            {
                bool interior = x >= candidate.RoomX && x < candidate.RoomX + candidate.RoomWidth
                    && z >= candidate.RoomZ && z < candidate.RoomZ + candidate.RoomDepth;
                if (interior)
                {
                    continue;
                }

                if (!grid.InBounds(x, z))
                {
                    return false;
                }

                if (grid.Get(x, z, upper) != DungeonCellKind.Empty)
                {
                    return false;
                }
            }
        }

        // Lower-floor stair cells: doorA on A's ring plus the three treads. Clear of the border and empty.
        if (!IsClearWalkableCell(grid, candidate.DoorA, lower))
        {
            return false;
        }

        if (!IsClearWalkableCell(grid, candidate.StairLower, lower))
        {
            return false;
        }

        if (!IsClearWalkableCell(grid, candidate.StairMid, lower))
        {
            return false;
        }

        if (!IsClearWalkableCell(grid, candidate.StairUpper, lower))
        {
            return false;
        }

        // Upper-floor cells: StairTop (room B's ring door) clear, and the open shaft above every tread empty.
        if (!IsClearWalkableCell(grid, candidate.StairTop, upper))
        {
            return false;
        }

        if (!IsClearWalkableCell(grid, candidate.VoidLower, upper))
        {
            return false;
        }

        if (!IsClearWalkableCell(grid, candidate.VoidMid, upper))
        {
            return false;
        }

        if (!IsClearWalkableCell(grid, candidate.VoidUpper, upper))
        {
            return false;
        }

        // The lower-floor run must not sit alongside any existing walkable cell other than the source interior it
        // leaves (same isolation rule as corridors). Room B's empty margin ring already isolates the upper floor.
        HashSet<(int X, int Z)> allowed = RoomInterior(source);
        if (HasForeignOrthogonalWalkable(grid, candidate.DoorA, lower, allowed))
        {
            return false;
        }

        if (HasForeignOrthogonalWalkable(grid, candidate.StairLower, lower, allowed))
        {
            return false;
        }

        if (HasForeignOrthogonalWalkable(grid, candidate.StairMid, lower, allowed))
        {
            return false;
        }

        if (HasForeignOrthogonalWalkable(grid, candidate.StairUpper, lower, allowed))
        {
            return false;
        }

        return true;
    }

    private static void CommitStair(
        Grid grid,
        DungeonRoom source,
        StairCandidate candidate,
        List<DungeonRoom> roomList,
        List<DungeonEdge> edgeList)
    {
        int newId = roomList.Count;
        var room = new DungeonRoom
        {
            Id = newId,
            Floor = candidate.Floor + 1,
            X = candidate.RoomX,
            Z = candidate.RoomZ,
            Width = candidate.RoomWidth,
            Depth = candidate.RoomDepth,
            RoomType = DungeonRoomType.Normal,
        };
        WriteRoom(grid, room);

        grid.Set(candidate.DoorA.X, candidate.DoorA.Z, candidate.DoorA.Floor, DungeonCellKind.DoorFrame);
        grid.Set(candidate.StairLower.X, candidate.StairLower.Z, candidate.StairLower.Floor, DungeonCellKind.StairLower);
        grid.Set(candidate.StairMid.X, candidate.StairMid.Z, candidate.StairMid.Floor, DungeonCellKind.StairMid);
        grid.Set(candidate.StairUpper.X, candidate.StairUpper.Z, candidate.StairUpper.Floor, DungeonCellKind.StairUpper);
        grid.Set(candidate.StairTop.X, candidate.StairTop.Z, candidate.StairTop.Floor, DungeonCellKind.StairTop);
        grid.Set(candidate.VoidLower.X, candidate.VoidLower.Z, candidate.VoidLower.Floor, DungeonCellKind.StairVoid);
        grid.Set(candidate.VoidMid.X, candidate.VoidMid.Z, candidate.VoidMid.Floor, DungeonCellKind.StairVoid);
        grid.Set(candidate.VoidUpper.X, candidate.VoidUpper.Z, candidate.VoidUpper.Floor, DungeonCellKind.StairVoid);

        roomList.Add(room);
        edgeList.Add(new DungeonEdge
        {
            RoomA = source.Id,
            RoomB = newId,
            Kind = DungeonEdgeKind.Stair,
            Path = new[] { candidate.StairLower, candidate.StairMid, candidate.StairUpper, candidate.StairTop },
            Doors = new[] { candidate.DoorA, candidate.StairTop },
        });
    }

    /// <summary>Counts the floors that hold at least one walkable cell, the real value for
    /// <see cref="LayoutStats.FloorsUsed"/>.</summary>
    internal static int CountFloorsUsed(DungeonCellKind[] cells, int width, int depth, int floors)
    {
        int used = 0;
        for (int f = 0; f < floors; f++)
        {
            bool any = false;
            for (int z = 0; z < depth && !any; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (DungeonLayout.IsWalkable(cells[(f * depth + z) * width + x]))
                    {
                        any = true;
                        break;
                    }
                }
            }

            if (any)
            {
                used++;
            }
        }

        return used;
    }

    private static void WriteRoom(Grid grid, DungeonRoom room)
    {
        for (int x = room.X; x < room.X + room.Width; x++)
        {
            for (int z = room.Z; z < room.Z + room.Depth; z++)
            {
                grid.Set(x, z, room.Floor, DungeonCellKind.RoomFloor);
            }
        }
    }

    /// <summary>A proposed same-floor room B reached from the source by a <see cref="DoorSrc"/>.Length-wide
    /// corridor. <see cref="DoorSrc"/>/<see cref="DoorNew"/> are the door-frame bands at each end (one cell each
    /// for a 1-wide corridor), and <see cref="Corridor"/> is the full corridor tube (band width x length).</summary>
    private readonly record struct Candidate(
        int Floor,
        int RoomX,
        int RoomZ,
        int RoomWidth,
        int RoomDepth,
        DungeonTile[] DoorSrc,
        DungeonTile[] DoorNew,
        DungeonTile[] Corridor);

    /// <summary>A proposed stair from a source room on <see cref="Floor"/> to a new room B on <see cref="Floor"/>
    /// + 1. The room rect (<see cref="RoomX"/>/<see cref="RoomZ"/>/<see cref="RoomWidth"/>/<see cref="RoomDepth"/>)
    /// lives on the upper floor; the three treads (<see cref="StairLower"/>, <see cref="StairMid"/>,
    /// <see cref="StairUpper"/>) are on the lower floor, the three <c>Void*</c> cells are the open shaft directly
    /// above them, and <see cref="StairTop"/> (one cell past the top tread) is both the run's landing and room
    /// B's ring door.</summary>
    private readonly record struct StairCandidate(
        int Floor,
        int RoomX,
        int RoomZ,
        int RoomWidth,
        int RoomDepth,
        DungeonTile DoorA,
        DungeonTile StairLower,
        DungeonTile StairMid,
        DungeonTile StairUpper,
        DungeonTile StairTop,
        DungeonTile VoidLower,
        DungeonTile VoidMid,
        DungeonTile VoidUpper);

    /// <summary>Thin bounds/indexing wrapper over a <see cref="DungeonCellKind"/> raster. Internal (not private)
    /// so <c>LoopPlanner</c> can wrap the already-grown raster via the second constructor and reuse
    /// <see cref="IsClearWalkableCell"/>/<see cref="HasForeignOrthogonalWalkable"/> instead of duplicating the
    /// bounds/adjacency arithmetic for loop-edge corridor validation.</summary>
    internal sealed class Grid
    {
        private readonly int _width;
        private readonly int _depth;

        internal Grid(int width, int depth, int floors)
        {
            _width = width;
            _depth = depth;
            Cells = new DungeonCellKind[width * depth * floors];
        }

        /// <summary>Wraps an existing (already-grown) raster in place, without allocating a new array. Used by
        /// <c>LoopPlanner</c>, which mutates <paramref name="cells"/> directly via <see cref="Set"/>.</summary>
        internal Grid(DungeonCellKind[] cells, int width, int depth)
        {
            _width = width;
            _depth = depth;
            Cells = cells;
        }

        internal DungeonCellKind[] Cells { get; }

        internal bool InBounds(int x, int z) => x >= 0 && x < _width && z >= 0 && z < _depth;

        internal bool InPlotWithMargin(int x, int z) => x >= 1 && x < _width - 1 && z >= 1 && z < _depth - 1;

        internal DungeonCellKind Get(int x, int z, int floor) => Cells[(floor * _depth + z) * _width + x];

        internal void Set(int x, int z, int floor, DungeonCellKind kind) => Cells[(floor * _depth + z) * _width + x] = kind;
    }
}
