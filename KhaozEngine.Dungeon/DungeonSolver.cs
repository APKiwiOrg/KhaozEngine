using System;
using System.Collections.Generic;

namespace KhaozEngine.Dungeon;

/// <summary>Result of <see cref="DungeonSolver.Verify"/>: whether a <see cref="DungeonLayout"/> is completable
/// by construction, plus every reason it is not. <see cref="Errors"/> is empty iff <see cref="IsSolvable"/> is
/// true.</summary>
/// <param name="IsSolvable">True when every room's every interior tile is reachable from the entrance (collecting
/// keys and unlocking locked doors as it goes) and, if a Boss room exists, the boss is reachable, and every
/// structural check in <see cref="DungeonSolver.Verify"/> passed.</param>
/// <param name="Errors">One entry per failing check, in the deterministic order <see cref="DungeonSolver.Verify"/>
/// ran them in. Empty when <see cref="IsSolvable"/> is true.</param>
public sealed record DungeonSolveReport(bool IsSolvable, IReadOnlyList<string> Errors);

/// <summary>
/// The always-on completability proof for a <see cref="DungeonLayout"/>. <see cref="Verify"/> is pure and
/// read-only: it never mutates the layout and draws no randomness, so the same layout always yields the same
/// report. <c>DungeonGenerator.Generate</c> calls it on every generated layout and throws if it fails, so an
/// un-completable dungeon can never leave the generator.
/// </summary>
public static class DungeonSolver
{
    /// <summary>Verifies that <paramref name="layout"/> is completable: a cell-level flood fill from the
    /// entrance, over walkable cells, following same-floor orthogonal adjacency plus the
    /// <see cref="DungeonCellKind.StairUpper"/>-to-<see cref="DungeonCellKind.StairTop"/> cross-floor adjacency,
    /// treating a locked edge's <see cref="DungeonEdge.Doors"/> cells as closed until the fill reaches an
    /// interior tile of the room holding its key (iterated fill-collect-unlock to a fixpoint). Solvable requires
    /// every room's every interior tile to end up reached (which covers the Boss room too, when one exists).
    /// Also runs four structural checks, independent of reachability: every edge's <see cref="DungeonEdge.Path"/>
    /// and <see cref="DungeonEdge.Doors"/> cells carry the cell kinds construction guarantees for that edge's
    /// <see cref="DungeonEdgeKind"/>, every <see cref="DungeonKeyPlacement.LockId"/> matches exactly one locked
    /// edge, every <see cref="DungeonRoom.Id"/> is unique, and no key sits inside its own lock's closed region
    /// (the belt-and-braces key-before-lock check: a key's room must stay reachable from the entrance when its
    /// lock's edge is removed). Any failing check appends a distinct error string. The layout is solvable iff
    /// none did.</summary>
    /// <param name="layout">The layout to verify. Never mutated.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    public static DungeonSolveReport Verify(DungeonLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var errors = new List<string>();

        CheckRoomIdsUnique(layout, errors);
        CheckEdgeCellKinds(layout, errors);
        CheckKeyLockMatching(layout, errors);
        CheckKeyNotBehindOwnLock(layout, errors);

        DungeonRoom? entrance = FindEntrance(layout);
        if (entrance is null)
        {
            errors.Add("No room with RoomType Entrance was found.");
            return new DungeonSolveReport(false, errors);
        }

        HashSet<(int X, int Z, int Floor)> reached = ComputeReachableCells(layout, entrance);

        foreach (DungeonRoom room in layout.Rooms)
        {
            if (!IsRoomFullyReached(room, reached))
            {
                errors.Add($"Room {room.Id} has an interior tile that is not reachable from the entrance.");
            }
        }

        return new DungeonSolveReport(errors.Count == 0, errors);
    }

    /// <summary>BFS room-graph distance (in edges, ignoring locks) from the entrance room to the Boss room, or to
    /// the farthest room when no Boss room exists. Used by <c>DungeonGenerator.Generate</c> to fill
    /// <see cref="LayoutStats.CriticalPathLength"/>. Returns 0 when there is no entrance room or only one
    /// room.</summary>
    internal static int ComputeCriticalPathLength(DungeonLayout layout)
    {
        DungeonRoom? entrance = FindEntrance(layout);
        if (entrance is null)
        {
            return 0;
        }

        Dictionary<int, List<int>> adjacency = BuildRoomAdjacency(layout);
        Dictionary<int, int> distances = BfsRoomDistances(adjacency, entrance.Id);

        DungeonRoom? boss = FindBoss(layout);
        if (boss is not null)
        {
            return distances.TryGetValue(boss.Id, out int bossDistance) ? bossDistance : 0;
        }

        int farthest = 0;
        foreach (KeyValuePair<int, int> pair in distances)
        {
            if (pair.Value > farthest)
            {
                farthest = pair.Value;
            }
        }

        return farthest;
    }

    private static DungeonRoom? FindEntrance(DungeonLayout layout)
    {
        foreach (DungeonRoom room in layout.Rooms)
        {
            if (room.RoomType == DungeonRoomType.Entrance)
            {
                return room;
            }
        }

        return null;
    }

    private static DungeonRoom? FindBoss(DungeonLayout layout)
    {
        foreach (DungeonRoom room in layout.Rooms)
        {
            if (room.RoomType == DungeonRoomType.Boss)
            {
                return room;
            }
        }

        return null;
    }

    private static void CheckRoomIdsUnique(DungeonLayout layout, List<string> errors)
    {
        var seen = new HashSet<int>();
        foreach (DungeonRoom room in layout.Rooms)
        {
            if (!seen.Add(room.Id))
            {
                errors.Add($"Room id {room.Id} is used by more than one room.");
            }
        }
    }

    private static readonly DungeonCellKind[] StairPathKinds =
    {
        DungeonCellKind.StairLower,
        DungeonCellKind.StairUpper,
        DungeonCellKind.StairTop,
    };

    private static readonly DungeonCellKind[] StairDoorKinds =
    {
        DungeonCellKind.DoorFrame,
        DungeonCellKind.StairTop,
    };

    private static void CheckEdgeCellKinds(DungeonLayout layout, List<string> errors)
    {
        foreach (DungeonEdge edge in layout.Edges)
        {
            if (edge.Kind == DungeonEdgeKind.Corridor)
            {
                CheckUniformCellKinds(layout, edge, edge.Path, DungeonCellKind.Corridor, "path", errors);
                CheckUniformCellKinds(layout, edge, edge.Doors, DungeonCellKind.DoorFrame, "door", errors);
            }
            else
            {
                CheckPositionalCellKinds(layout, edge, edge.Path, StairPathKinds, "stair path", errors);
                CheckPositionalCellKinds(layout, edge, edge.Doors, StairDoorKinds, "stair door", errors);
            }
        }
    }

    private static void CheckUniformCellKinds(
        DungeonLayout layout,
        DungeonEdge edge,
        IReadOnlyList<DungeonTile> tiles,
        DungeonCellKind expected,
        string role,
        List<string> errors)
    {
        foreach (DungeonTile tile in tiles)
        {
            DungeonCellKind actual = layout.GetCell(tile.X, tile.Z, tile.Floor);
            if (actual != expected)
            {
                errors.Add(
                    $"Edge {edge.RoomA}->{edge.RoomB} {role} cell ({tile.X},{tile.Z},{tile.Floor}) is {actual}, expected {expected}.");
            }
        }
    }

    private static void CheckPositionalCellKinds(
        DungeonLayout layout,
        DungeonEdge edge,
        IReadOnlyList<DungeonTile> tiles,
        DungeonCellKind[] expectedKinds,
        string role,
        List<string> errors)
    {
        if (tiles.Count != expectedKinds.Length)
        {
            errors.Add($"Edge {edge.RoomA}->{edge.RoomB} {role} has {tiles.Count} cells, expected {expectedKinds.Length}.");
            return;
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            DungeonTile tile = tiles[i];
            DungeonCellKind actual = layout.GetCell(tile.X, tile.Z, tile.Floor);
            DungeonCellKind expected = expectedKinds[i];
            if (actual != expected)
            {
                errors.Add(
                    $"Edge {edge.RoomA}->{edge.RoomB} {role} cell ({tile.X},{tile.Z},{tile.Floor}) is {actual}, expected {expected}.");
            }
        }
    }

    private static void CheckKeyLockMatching(DungeonLayout layout, List<string> errors)
    {
        foreach (DungeonKeyPlacement key in layout.Keys)
        {
            int matches = 0;
            foreach (DungeonEdge edge in layout.Edges)
            {
                if (edge.LockId.HasValue && edge.LockId.Value == key.LockId)
                {
                    matches++;
                }
            }

            if (matches != 1)
            {
                errors.Add($"Lock {key.LockId} (key in room {key.RoomId}) matches {matches} locked edges, expected exactly 1.");
            }
        }
    }

    /// <summary>Belt-and-braces key-before-lock check, independent of the cell-level fixpoint fill: for every key
    /// placement, the key's room must remain reachable from the entrance over the room graph once the single
    /// locked edge that key opens is removed. Removing a lock's bridge edge exposes the region it closes off, so a
    /// key found unreachable there sits behind the very lock it is meant to open. Appends a distinct error naming
    /// the offending lock, key room, and its "closed region". A layout with no entrance or no keys is a
    /// no-op.</summary>
    private static void CheckKeyNotBehindOwnLock(DungeonLayout layout, List<string> errors)
    {
        if (layout.Keys.Count == 0)
        {
            return;
        }

        DungeonRoom? entrance = FindEntrance(layout);
        if (entrance is null)
        {
            return;
        }

        foreach (DungeonKeyPlacement key in layout.Keys)
        {
            DungeonEdge? lockEdge = null;
            foreach (DungeonEdge edge in layout.Edges)
            {
                if (edge.LockId.HasValue && edge.LockId.Value == key.LockId)
                {
                    lockEdge = edge;
                    break;
                }
            }

            if (lockEdge is null)
            {
                // CheckKeyLockMatching already reports a key whose lock has no (or many) edges.
                continue;
            }

            HashSet<int> reachable = ReachableRoomsExcludingEdge(layout, entrance.Id, lockEdge);
            if (!reachable.Contains(key.RoomId))
            {
                errors.Add(
                    $"Lock {key.LockId}'s key in room {key.RoomId} is inside that lock's own closed region (unreachable from the entrance once the lock's edge is removed).");
            }
        }
    }

    /// <summary>Rooms reachable from <paramref name="entranceId"/> over the room graph with the single
    /// <paramref name="excludedEdge"/> removed. Used only by <see cref="CheckKeyNotBehindOwnLock"/>.</summary>
    private static HashSet<int> ReachableRoomsExcludingEdge(DungeonLayout layout, int entranceId, DungeonEdge excludedEdge)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (DungeonRoom room in layout.Rooms)
        {
            adjacency[room.Id] = new List<int>();
        }

        foreach (DungeonEdge edge in layout.Edges)
        {
            if (ReferenceEquals(edge, excludedEdge))
            {
                continue;
            }

            adjacency[edge.RoomA].Add(edge.RoomB);
            adjacency[edge.RoomB].Add(edge.RoomA);
        }

        var reached = new HashSet<int> { entranceId };
        var queue = new Queue<int>();
        queue.Enqueue(entranceId);
        while (queue.Count > 0)
        {
            foreach (int next in adjacency[queue.Dequeue()])
            {
                if (reached.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reached;
    }

    private static bool IsRoomFullyReached(DungeonRoom room, HashSet<(int X, int Z, int Floor)> reached)
    {
        for (int x = room.X; x < room.X + room.Width; x++)
        {
            for (int z = room.Z; z < room.Z + room.Depth; z++)
            {
                if (!reached.Contains((x, z, room.Floor)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool RoomHasAnyReachedInteriorTile(DungeonRoom room, HashSet<(int X, int Z, int Floor)> reached)
    {
        for (int x = room.X; x < room.X + room.Width; x++)
        {
            for (int z = room.Z; z < room.Z + room.Depth; z++)
            {
                if (reached.Contains((x, z, room.Floor)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Runs the fill-collect-unlock fixpoint: flood fill from the entrance with the locks known so far,
    /// then unlock every lock whose key room the fill reached, then fill again, until a fill adds no newly
    /// unlocked lock. Returns the final reached-cell set.</summary>
    private static HashSet<(int X, int Z, int Floor)> ComputeReachableCells(DungeonLayout layout, DungeonRoom entrance)
    {
        Dictionary<(int X, int Z, int Floor), int> lockedDoorTiles = BuildLockedDoorTiles(layout);
        var unlockedLocks = new HashSet<int>();
        var start = new DungeonTile(entrance.X, entrance.Z, entrance.Floor);

        HashSet<(int X, int Z, int Floor)> reached = FloodFill(layout, start, lockedDoorTiles, unlockedLocks);

        bool unlockedNewLock = true;
        while (unlockedNewLock)
        {
            unlockedNewLock = false;
            foreach (DungeonRoom room in layout.Rooms)
            {
                if (!RoomHasAnyReachedInteriorTile(room, reached))
                {
                    continue;
                }

                foreach (DungeonKeyPlacement key in layout.Keys)
                {
                    if (key.RoomId == room.Id && unlockedLocks.Add(key.LockId))
                    {
                        unlockedNewLock = true;
                    }
                }
            }

            if (unlockedNewLock)
            {
                reached = FloodFill(layout, start, lockedDoorTiles, unlockedLocks);
            }
        }

        return reached;
    }

    private static Dictionary<(int X, int Z, int Floor), int> BuildLockedDoorTiles(DungeonLayout layout)
    {
        var lockedDoorTiles = new Dictionary<(int X, int Z, int Floor), int>();
        foreach (DungeonEdge edge in layout.Edges)
        {
            if (!edge.LockId.HasValue)
            {
                continue;
            }

            foreach (DungeonTile door in edge.Doors)
            {
                lockedDoorTiles[(door.X, door.Z, door.Floor)] = edge.LockId.Value;
            }
        }

        return lockedDoorTiles;
    }

    private static HashSet<(int X, int Z, int Floor)> FloodFill(
        DungeonLayout layout,
        DungeonTile start,
        Dictionary<(int X, int Z, int Floor), int> lockedDoorTiles,
        HashSet<int> unlockedLocks)
    {
        var reached = new HashSet<(int X, int Z, int Floor)>();
        var queue = new Queue<DungeonTile>();

        var startKey = (start.X, start.Z, start.Floor);
        reached.Add(startKey);
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            DungeonTile tile = queue.Dequeue();
            foreach (DungeonTile neighbor in Neighbors(layout, tile))
            {
                var key = (neighbor.X, neighbor.Z, neighbor.Floor);
                if (reached.Contains(key))
                {
                    continue;
                }

                DungeonCellKind kind = layout.GetCell(neighbor.X, neighbor.Z, neighbor.Floor);
                if (!DungeonLayout.IsWalkable(kind))
                {
                    continue;
                }

                if (lockedDoorTiles.TryGetValue(key, out int lockId) && !unlockedLocks.Contains(lockId))
                {
                    continue;
                }

                reached.Add(key);
                queue.Enqueue(neighbor);
            }
        }

        return reached;
    }

    /// <summary>The same-floor orthogonal neighbors of <paramref name="tile"/>, plus the single cross-floor
    /// adjacency the geometry model allows: a <see cref="DungeonCellKind.StairUpper"/> cell connects to the
    /// <see cref="DungeonCellKind.StairTop"/> cell directly above it, and vice versa.</summary>
    private static IEnumerable<DungeonTile> Neighbors(DungeonLayout layout, DungeonTile tile)
    {
        yield return tile with { X = tile.X + 1 };
        yield return tile with { X = tile.X - 1 };
        yield return tile with { Z = tile.Z + 1 };
        yield return tile with { Z = tile.Z - 1 };

        DungeonCellKind kind = layout.GetCell(tile.X, tile.Z, tile.Floor);
        if (kind == DungeonCellKind.StairUpper)
        {
            var above = tile with { Floor = tile.Floor + 1 };
            if (layout.GetCell(above.X, above.Z, above.Floor) == DungeonCellKind.StairTop)
            {
                yield return above;
            }
        }
        else if (kind == DungeonCellKind.StairTop)
        {
            var below = tile with { Floor = tile.Floor - 1 };
            if (layout.GetCell(below.X, below.Z, below.Floor) == DungeonCellKind.StairUpper)
            {
                yield return below;
            }
        }
    }

    private static Dictionary<int, List<int>> BuildRoomAdjacency(DungeonLayout layout)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (DungeonRoom room in layout.Rooms)
        {
            adjacency[room.Id] = new List<int>();
        }

        foreach (DungeonEdge edge in layout.Edges)
        {
            adjacency[edge.RoomA].Add(edge.RoomB);
            adjacency[edge.RoomB].Add(edge.RoomA);
        }

        return adjacency;
    }

    private static Dictionary<int, int> BfsRoomDistances(Dictionary<int, List<int>> adjacency, int fromId)
    {
        var distances = new Dictionary<int, int> { [fromId] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(fromId);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            int distance = distances[id];
            foreach (int next in adjacency[id])
            {
                if (!distances.ContainsKey(next))
                {
                    distances[next] = distance + 1;
                    queue.Enqueue(next);
                }
            }
        }

        return distances;
    }
}
