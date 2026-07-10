using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Dungeon.Internal;

/// <summary>
/// Post-growth, pre-wall-pass loop-edge phase for <c>DungeonGenerator.Generate</c>. <see cref="PlanLoopEdges"/>
/// adds up to <see cref="DungeonConfig.LoopEdgeBudget"/> extra same-floor <see cref="DungeonEdgeKind.Corridor"/>
/// edges between rooms the spanning tree already grew, turning it into a graph with cycles. A candidate pairs
/// two rooms on the same floor whose margin rings face each other closely enough for a straight, 1-to-4-cell
/// corridor (the same cell-level validation as growth corridors, reusing <see cref="RoomGrower"/>'s helpers),
/// whose BFS graph distance over the edges as grown is at least 4, and which have no existing edge between
/// them. Candidates are built once, in deterministic ascending (roomIdA, roomIdB) order. The <c>rooms</c> RNG
/// stream then repeatedly draws <c>rooms.Next(candidates.Count)</c>, removes the picked candidate, and (since
/// an earlier commit in this same phase may have carved cells the candidate's geometry now collides with)
/// re-validates its geometry against the live raster before committing, skipping a now-stale candidate without
/// counting it against the budget. Stops when the budget is spent or the candidate list is exhausted. A budget
/// of zero (or fewer than two same-floor rooms) is a no-op that draws nothing from <c>rooms</c>, so it never
/// perturbs the RNG stream Task 3/4 configs (which set <see cref="DungeonConfig.LoopEdgeBudget"/> to zero) rely
/// on.
/// </summary>
internal static class LoopPlanner
{
    /// <summary>Plans and commits loop edges into <paramref name="grown"/> in place: writes corridor and door
    /// cells into its raster and appends to its edge list. Deterministic in <paramref name="config"/>, the
    /// grow result, and the <paramref name="rooms"/> stream. Draws nothing from <paramref name="rooms"/> when
    /// <see cref="DungeonConfig.LoopEdgeBudget"/> is zero.</summary>
    internal static void PlanLoopEdges(DungeonConfig config, DeterministicRng rooms, GrowResult grown)
    {
        if (config.LoopEdgeBudget <= 0)
        {
            return;
        }

        int width = config.PlotWidthTiles;
        int depth = config.PlotDepthTiles;

        List<LoopCandidate> candidates = BuildCandidates(grown, width, depth);

        int budget = config.LoopEdgeBudget;
        while (budget > 0 && candidates.Count > 0)
        {
            int pick = rooms.Next(candidates.Count);
            LoopCandidate candidate = candidates[pick];
            candidates.RemoveAt(pick);

            DungeonRoom roomA = grown.Rooms[candidate.RoomAId];
            DungeonRoom roomB = grown.Rooms[candidate.RoomBId];
            if (!ValidateGeometry(grown.Cells, width, depth, candidate.Floor, candidate.DoorA, candidate.DoorB, candidate.Corridor, roomA, roomB))
            {
                // Stale: an earlier commit in this phase carved a cell this candidate's geometry needs.
                // Already removed above, so it is deterministically skipped without spending the budget.
                continue;
            }

            Commit(grown, width, depth, candidate);
            budget--;
        }
    }

    private static List<LoopCandidate> BuildCandidates(GrowResult grown, int width, int depth)
    {
        List<DungeonRoom> roomList = grown.Rooms;
        var result = new List<LoopCandidate>();

        var existingEdges = new HashSet<(int A, int B)>();
        foreach (DungeonEdge edge in grown.Edges)
        {
            existingEdges.Add(edge.RoomA < edge.RoomB ? (edge.RoomA, edge.RoomB) : (edge.RoomB, edge.RoomA));
        }

        Dictionary<int, List<int>> adjacency = BuildAdjacency(roomList, grown.Edges);

        // Deterministic scan order: ascending (roomIdA, roomIdB). Room ids are assigned in growth order, so
        // iterating the room list with i < j visits every pair exactly once with roomIdA < roomIdB.
        for (int i = 0; i < roomList.Count; i++)
        {
            DungeonRoom a = roomList[i];
            for (int j = i + 1; j < roomList.Count; j++)
            {
                DungeonRoom b = roomList[j];
                if (a.Floor != b.Floor)
                {
                    continue;
                }

                if (existingEdges.Contains((a.Id, b.Id)))
                {
                    continue;
                }

                if (BfsDistance(adjacency, a.Id, b.Id) < 4)
                {
                    continue;
                }

                LoopCandidate? candidate = TryBuildGeometry(grown.Cells, width, depth, a, b);
                if (candidate.HasValue)
                {
                    result.Add(candidate.Value);
                }
            }
        }

        return result;
    }

    /// <summary>Tries to find a straight, 1-to-4-cell corridor joining <paramref name="a"/> and
    /// <paramref name="b"/>'s facing margin rings. The two rooms don't overlap, so if their Z ranges overlap
    /// they must be split apart in X (a horizontal corridor), and if their X ranges overlap they must be split
    /// apart in Z (a vertical corridor). Neither overlapping means they aren't facing at all. When the gap
    /// yields a valid length, scans the facing line range in ascending order and returns the first line whose
    /// full corridor + door geometry validates against the current raster.</summary>
    private static LoopCandidate? TryBuildGeometry(DungeonCellKind[] cells, int width, int depth, DungeonRoom a, DungeonRoom b)
    {
        int floor = a.Floor;

        int zLo = Math.Max(a.Z, b.Z);
        int zHi = Math.Min(a.Z + a.Depth - 1, b.Z + b.Depth - 1);
        if (zLo <= zHi)
        {
            DungeonRoom west;
            DungeonRoom east;
            if (a.X + a.Width <= b.X)
            {
                west = a;
                east = b;
            }
            else if (b.X + b.Width <= a.X)
            {
                west = b;
                east = a;
            }
            else
            {
                return null;
            }

            int length = east.X - (west.X + west.Width) - 2;
            if (length < 1 || length > 4)
            {
                return null;
            }

            for (int lineZ = zLo; lineZ <= zHi; lineZ++)
            {
                var doorWest = new DungeonTile(west.X + west.Width, lineZ, floor);
                var doorEast = new DungeonTile(east.X - 1, lineZ, floor);
                var corridor = new DungeonTile[length];
                for (int k = 0; k < length; k++)
                {
                    corridor[k] = new DungeonTile(doorWest.X + 1 + k, lineZ, floor);
                }

                if (ValidateGeometry(cells, width, depth, floor, doorWest, doorEast, corridor, west, east))
                {
                    return west.Id == a.Id
                        ? new LoopCandidate(a.Id, b.Id, floor, doorWest, doorEast, corridor)
                        : new LoopCandidate(a.Id, b.Id, floor, doorEast, doorWest, corridor);
                }
            }

            return null;
        }

        int xLo = Math.Max(a.X, b.X);
        int xHi = Math.Min(a.X + a.Width - 1, b.X + b.Width - 1);
        if (xLo <= xHi)
        {
            DungeonRoom north;
            DungeonRoom south;
            if (a.Z + a.Depth <= b.Z)
            {
                north = a;
                south = b;
            }
            else if (b.Z + b.Depth <= a.Z)
            {
                north = b;
                south = a;
            }
            else
            {
                return null;
            }

            int length = south.Z - (north.Z + north.Depth) - 2;
            if (length < 1 || length > 4)
            {
                return null;
            }

            for (int lineX = xLo; lineX <= xHi; lineX++)
            {
                var doorNorth = new DungeonTile(lineX, north.Z + north.Depth, floor);
                var doorSouth = new DungeonTile(lineX, south.Z - 1, floor);
                var corridor = new DungeonTile[length];
                for (int k = 0; k < length; k++)
                {
                    corridor[k] = new DungeonTile(lineX, doorNorth.Z + 1 + k, floor);
                }

                if (ValidateGeometry(cells, width, depth, floor, doorNorth, doorSouth, corridor, north, south))
                {
                    return north.Id == a.Id
                        ? new LoopCandidate(a.Id, b.Id, floor, doorNorth, doorSouth, corridor)
                        : new LoopCandidate(a.Id, b.Id, floor, doorSouth, doorNorth, corridor);
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>Same cell-level validation growth corridors use (via <see cref="RoomGrower"/>'s shared
    /// helpers): both door cells and every corridor cell must be in-plot-with-margin and currently
    /// <see cref="DungeonCellKind.Empty"/>, and none may be orthogonally adjacent to a walkable cell outside
    /// <paramref name="roomA"/>'s and <paramref name="roomB"/>'s own interiors (the two rooms the loop edge
    /// joins).</summary>
    private static bool ValidateGeometry(
        DungeonCellKind[] cells,
        int width,
        int depth,
        int floor,
        DungeonTile doorA,
        DungeonTile doorB,
        DungeonTile[] corridor,
        DungeonRoom roomA,
        DungeonRoom roomB)
    {
        var grid = new RoomGrower.Grid(cells, width, depth);

        if (!RoomGrower.IsClearWalkableCell(grid, doorA, floor))
        {
            return false;
        }

        if (!RoomGrower.IsClearWalkableCell(grid, doorB, floor))
        {
            return false;
        }

        foreach (DungeonTile tile in corridor)
        {
            if (!RoomGrower.IsClearWalkableCell(grid, tile, floor))
            {
                return false;
            }
        }

        HashSet<(int X, int Z)> allowed = RoomGrower.RoomInterior(roomA);
        allowed.UnionWith(RoomGrower.RoomInterior(roomB));

        if (RoomGrower.HasForeignOrthogonalWalkable(grid, doorA, floor, allowed))
        {
            return false;
        }

        if (RoomGrower.HasForeignOrthogonalWalkable(grid, doorB, floor, allowed))
        {
            return false;
        }

        foreach (DungeonTile tile in corridor)
        {
            if (RoomGrower.HasForeignOrthogonalWalkable(grid, tile, floor, allowed))
            {
                return false;
            }
        }

        return true;
    }

    private static void Commit(GrowResult grown, int width, int depth, LoopCandidate candidate)
    {
        var grid = new RoomGrower.Grid(grown.Cells, width, depth);
        int floor = candidate.Floor;

        foreach (DungeonTile tile in candidate.Corridor)
        {
            grid.Set(tile.X, tile.Z, floor, DungeonCellKind.Corridor);
        }

        grid.Set(candidate.DoorA.X, candidate.DoorA.Z, floor, DungeonCellKind.DoorFrame);
        grid.Set(candidate.DoorB.X, candidate.DoorB.Z, floor, DungeonCellKind.DoorFrame);

        grown.Edges.Add(new DungeonEdge
        {
            RoomA = candidate.RoomAId,
            RoomB = candidate.RoomBId,
            Kind = DungeonEdgeKind.Corridor,
            Path = candidate.Corridor,
            Doors = new[] { candidate.DoorA, candidate.DoorB },
        });
    }

    private static Dictionary<int, List<int>> BuildAdjacency(List<DungeonRoom> roomList, List<DungeonEdge> edgeList)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (DungeonRoom room in roomList)
        {
            adjacency[room.Id] = new List<int>();
        }

        foreach (DungeonEdge edge in edgeList)
        {
            adjacency[edge.RoomA].Add(edge.RoomB);
            adjacency[edge.RoomB].Add(edge.RoomA);
        }

        return adjacency;
    }

    /// <summary>BFS shortest-path edge count between <paramref name="fromId"/> and <paramref name="toId"/> over
    /// <paramref name="adjacency"/>. The grow result is always connected, so an unreachable result never
    /// occurs in practice. <see cref="int.MaxValue"/> guards it defensively anyway.</summary>
    private static int BfsDistance(Dictionary<int, List<int>> adjacency, int fromId, int toId)
    {
        if (fromId == toId)
        {
            return 0;
        }

        var visited = new HashSet<int> { fromId };
        var queue = new Queue<(int Id, int Dist)>();
        queue.Enqueue((fromId, 0));

        while (queue.Count > 0)
        {
            (int id, int dist) = queue.Dequeue();
            foreach (int next in adjacency[id])
            {
                if (next == toId)
                {
                    return dist + 1;
                }

                if (visited.Add(next))
                {
                    queue.Enqueue((next, dist + 1));
                }
            }
        }

        return int.MaxValue;
    }

    /// <summary>A validated, ready-to-commit loop edge: the room pair (<see cref="RoomAId"/> &lt;
    /// <see cref="RoomBId"/>), its floor, its two door-frame cells (<see cref="DoorA"/> belongs to
    /// <see cref="RoomAId"/>, <see cref="DoorB"/> to <see cref="RoomBId"/>), and its corridor path.</summary>
    private readonly record struct LoopCandidate(
        int RoomAId,
        int RoomBId,
        int Floor,
        DungeonTile DoorA,
        DungeonTile DoorB,
        DungeonTile[] Corridor);
}
