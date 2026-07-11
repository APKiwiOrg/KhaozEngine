using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Dungeon.Internal;

/// <summary>Output of one <see cref="GatingPlanner.PlanGating"/> pass: the key placements it appended (one per
/// lock it placed) and how many locks it actually placed. The planner also mutates the room graph in place,
/// setting the Boss room's type, the Key rooms' types, and the locked edges' <see cref="DungeonEdge.LockId"/>
/// values.</summary>
internal sealed record GatingResult(List<DungeonKeyPlacement> Keys, int LocksPlaced);

/// <summary>
/// Completability-critical gating phase for <c>DungeonGenerator.Generate</c>, run after loops and the wall pass
/// and before the solver re-proves the result. <see cref="PlanGating"/> marks the Boss room (the farthest room
/// from the entrance by BFS edge-distance), then places up to <see cref="DungeonConfig.LockCount"/> locks on
/// bridge edges that lie on the entrance-to-boss critical path, ordered from the entrance outward. Each lock's
/// key is placed in a room that is provably reachable without crossing that lock (reachability computed with the
/// lock and every deeper lock removed), so keys are collectable before their locks by construction. The solver
/// then re-proves the whole layout via its fill-collect-unlock fixpoint. Deterministic in the config and the
/// gating RNG stream: it draws only when it places at least one lock, so a config with no boss and no locks
/// perturbs nothing and leaves earlier-task layouts byte-identical.
/// </summary>
internal static class GatingPlanner
{
    /// <summary>Plans gating into <paramref name="rooms"/> and <paramref name="edges"/> in place: sets the Boss
    /// and Key room types and the locked edges' <see cref="DungeonEdge.LockId"/> values, and returns the key
    /// placements plus the lock count. Draws from <paramref name="gating"/> only while placing keys, so a config
    /// with <see cref="DungeonConfig.BossRoom"/> false and <see cref="DungeonConfig.LockCount"/> zero returns an
    /// empty result without drawing or mutating anything.</summary>
    internal static GatingResult PlanGating(
        DungeonConfig config,
        DeterministicRng gating,
        List<DungeonRoom> rooms,
        List<DungeonEdge> edges)
    {
        var keys = new List<DungeonKeyPlacement>();

        // Nothing to gate: guard before any draw so LockCount=0/BossRoom=false layouts stay byte-identical.
        if (!config.BossRoom && config.LockCount <= 0)
        {
            return new GatingResult(keys, 0);
        }

        DungeonRoom? entrance = FindEntrance(rooms);
        if (entrance is null)
        {
            return new GatingResult(keys, 0);
        }

        Dictionary<int, List<(int Neighbor, int EdgeIndex)>> adjacency = BuildAdjacency(rooms, edges);
        BfsResult bfs = Bfs(adjacency, entrance.Id);

        // Boss = farthest room from the entrance (tie: lowest id). Only when the config asks for it and there
        // are at least three rooms to justify a critical path.
        int? bossId = null;
        if (config.BossRoom && rooms.Count >= 3)
        {
            bossId = FarthestRoom(rooms, bfs.Distance);
            if (bossId.HasValue)
            {
                rooms[IndexOfId(rooms, bossId.Value)].RoomType = DungeonRoomType.Boss;
            }
        }

        if (config.LockCount <= 0)
        {
            return new GatingResult(keys, 0);
        }

        // Lock candidates = bridge edges on the entrance-to-target BFS path, ordered from the entrance outward.
        // Target is the boss when one exists, else the farthest room.
        int? targetId = bossId ?? FarthestRoom(rooms, bfs.Distance);
        if (!targetId.HasValue || targetId.Value == entrance.Id)
        {
            return new GatingResult(keys, 0);
        }

        HashSet<int> bridges = FindBridgeEdges(rooms, adjacency);
        List<int> pathEdges = ReconstructPathEdges(bfs, entrance.Id, targetId.Value);

        var lockEdges = new List<int>();
        foreach (int edgeIndex in pathEdges)
        {
            if (bridges.Contains(edgeIndex))
            {
                lockEdges.Add(edgeIndex);
                if (lockEdges.Count >= config.LockCount)
                {
                    break;
                }
            }
        }

        int placed = lockEdges.Count;
        for (int i = 0; i < placed; i++)
        {
            // LockId is 1-based, ascending with distance from the entrance: lock 1 is nearest the entrance.
            edges[lockEdges[i]].LockId = i + 1;
        }

        for (int lockId = 1; lockId <= placed; lockId++)
        {
            // Rooms reachable with this lock and every deeper lock removed: the region strictly in front of this
            // lock, where its key can sit and still be collectable before the lock is ever required.
            HashSet<int> reachable = ReachableRoomsExcludingLocks(rooms, edges, entrance.Id, lockId);

            List<int> candidates = BuildKeyRoomCandidates(reachable, entrance.Id);
            int pick = candidates[gating.Next(candidates.Count)];

            DungeonRoom pickRoom = rooms[IndexOfId(rooms, pick)];
            if (pickRoom.RoomType == DungeonRoomType.Normal)
            {
                pickRoom.RoomType = DungeonRoomType.Key;
            }

            keys.Add(new DungeonKeyPlacement { LockId = lockId, RoomId = pick });
        }

        return new GatingResult(keys, placed);
    }

    private static DungeonRoom? FindEntrance(List<DungeonRoom> rooms)
    {
        foreach (DungeonRoom room in rooms)
        {
            if (room.RoomType == DungeonRoomType.Entrance)
            {
                return room;
            }
        }

        return null;
    }

    private static Dictionary<int, List<(int Neighbor, int EdgeIndex)>> BuildAdjacency(
        List<DungeonRoom> rooms,
        List<DungeonEdge> edges)
    {
        var adjacency = new Dictionary<int, List<(int Neighbor, int EdgeIndex)>>();
        foreach (DungeonRoom room in rooms)
        {
            adjacency[room.Id] = new List<(int, int)>();
        }

        for (int i = 0; i < edges.Count; i++)
        {
            DungeonEdge edge = edges[i];
            adjacency[edge.RoomA].Add((edge.RoomB, i));
            adjacency[edge.RoomB].Add((edge.RoomA, i));
        }

        // Stable neighbor order (ascending room id) so the BFS tree, and thus the reconstructed path, is
        // deterministic regardless of edge insertion order.
        foreach (List<(int Neighbor, int EdgeIndex)> list in adjacency.Values)
        {
            list.Sort((a, b) => a.Neighbor.CompareTo(b.Neighbor));
        }

        return adjacency;
    }

    private readonly record struct BfsResult(
        Dictionary<int, int> Distance,
        Dictionary<int, int> ParentRoom,
        Dictionary<int, int> ParentEdge);

    private static BfsResult Bfs(Dictionary<int, List<(int Neighbor, int EdgeIndex)>> adjacency, int startId)
    {
        var distance = new Dictionary<int, int> { [startId] = 0 };
        var parentRoom = new Dictionary<int, int>();
        var parentEdge = new Dictionary<int, int>();
        var queue = new Queue<int>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            foreach ((int neighbor, int edgeIndex) in adjacency[id])
            {
                if (distance.ContainsKey(neighbor))
                {
                    continue;
                }

                distance[neighbor] = distance[id] + 1;
                parentRoom[neighbor] = id;
                parentEdge[neighbor] = edgeIndex;
                queue.Enqueue(neighbor);
            }
        }

        return new BfsResult(distance, parentRoom, parentEdge);
    }

    private static int? FarthestRoom(List<DungeonRoom> rooms, Dictionary<int, int> distance)
    {
        int bestId = -1;
        int bestDist = -1;
        foreach (DungeonRoom room in rooms)
        {
            if (!distance.TryGetValue(room.Id, out int dist))
            {
                continue;
            }

            // Strict greater keeps the lowest id on ties, since rooms are visited in ascending id order.
            if (dist > bestDist)
            {
                bestDist = dist;
                bestId = room.Id;
            }
        }

        return bestId < 0 ? null : bestId;
    }

    private static List<int> ReconstructPathEdges(BfsResult bfs, int entranceId, int targetId)
    {
        var reversed = new List<int>();
        int node = targetId;
        while (node != entranceId && bfs.ParentEdge.TryGetValue(node, out int edgeIndex))
        {
            reversed.Add(edgeIndex);
            node = bfs.ParentRoom[node];
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>Tarjan bridge detection over the simple room graph (no parallel edges exist: growth is a tree and
    /// loops never duplicate an existing pair). Returns the set of edge indices whose removal disconnects the
    /// graph.</summary>
    private static HashSet<int> FindBridgeEdges(
        List<DungeonRoom> rooms,
        Dictionary<int, List<(int Neighbor, int EdgeIndex)>> adjacency)
    {
        var bridges = new HashSet<int>();
        var disc = new Dictionary<int, int>();
        var low = new Dictionary<int, int>();
        int timer = 0;

        foreach (DungeonRoom room in rooms)
        {
            if (!disc.ContainsKey(room.Id))
            {
                BridgeDfs(room.Id, -1, adjacency, disc, low, bridges, ref timer);
            }
        }

        return bridges;
    }

    private static void BridgeDfs(
        int u,
        int parentEdge,
        Dictionary<int, List<(int Neighbor, int EdgeIndex)>> adjacency,
        Dictionary<int, int> disc,
        Dictionary<int, int> low,
        HashSet<int> bridges,
        ref int timer)
    {
        disc[u] = timer;
        low[u] = timer;
        timer++;

        foreach ((int v, int edgeIndex) in adjacency[u])
        {
            if (edgeIndex == parentEdge)
            {
                continue;
            }

            if (disc.TryGetValue(v, out int discV))
            {
                low[u] = Math.Min(low[u], discV);
            }
            else
            {
                BridgeDfs(v, edgeIndex, adjacency, disc, low, bridges, ref timer);
                low[u] = Math.Min(low[u], low[v]);
                if (low[v] > disc[u])
                {
                    bridges.Add(edgeIndex);
                }
            }
        }
    }

    /// <summary>Rooms reachable from the entrance over the room graph with every locked edge whose
    /// <see cref="DungeonEdge.LockId"/> is at least <paramref name="minLockId"/> removed. Because the locks sit on
    /// bridge edges ordered outward from the entrance, this is exactly the region in front of lock
    /// <paramref name="minLockId"/>.</summary>
    private static HashSet<int> ReachableRoomsExcludingLocks(
        List<DungeonRoom> rooms,
        List<DungeonEdge> edges,
        int entranceId,
        int minLockId)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (DungeonRoom room in rooms)
        {
            adjacency[room.Id] = new List<int>();
        }

        foreach (DungeonEdge edge in edges)
        {
            if (edge.LockId.HasValue && edge.LockId.Value >= minLockId)
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

    private static List<int> BuildKeyRoomCandidates(HashSet<int> reachable, int entranceId)
    {
        var candidates = new List<int>();
        foreach (int id in reachable)
        {
            if (id != entranceId)
            {
                candidates.Add(id);
            }
        }

        // Exclude the entrance only when the region offers another room. Otherwise the entrance is the sole
        // legal home for the key.
        if (candidates.Count == 0)
        {
            candidates.Add(entranceId);
        }

        candidates.Sort();
        return candidates;
    }

    private static int IndexOfId(List<DungeonRoom> rooms, int id)
    {
        // Room ids are assigned in growth order (id == list index), but resolve defensively rather than assume it.
        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Id == id)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Room id {id} not found in the room list.");
    }
}
