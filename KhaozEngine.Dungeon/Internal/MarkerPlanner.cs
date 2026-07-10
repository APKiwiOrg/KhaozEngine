using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Dungeon.Internal;

/// <summary>
/// Typed marker planning for <c>DungeonGenerator.Generate</c>: the last content phase, run after gating and
/// before the layout is assembled and re-proven by the solver. Markers are pure tagged data appended to
/// <see cref="DungeonLayout.Markers"/>. <see cref="PlanMarkers"/> never touches the cell grid, room list,
/// edge list, or key placements, so markers cannot affect completability or gating. Draws from its own
/// <c>markers</c> RNG stream, derived after <c>rooms</c> and <c>gating</c>, so retuning marker density never
/// reshuffles growth or gating (see <c>DungeonMarkerTests.MarkerStream_Isolated</c>).
/// </summary>
internal static class MarkerPlanner
{
    /// <summary>Plans every marker for <paramref name="rooms"/>, in this order: an <see cref="DungeonMarkerType.Entrance"/>
    /// marker at the Entrance room's center tile (tag "entrance"), an <see cref="DungeonMarkerType.Objective"/>
    /// marker at the Boss room's center tile when one exists (tag "boss"), then per non-entrance room (in
    /// stored list order) a <c>markers.Next(SpawnMarkersPerRoomMax + 1)</c> count of Spawn markers (tag
    /// "spawn") and a <c>markers.Next(LootMarkersPerRoomMax + 1)</c> count of Loot markers (tag "loot", plus
    /// "treasure" when the room is a Key or Treasure room), placed on distinct interior tiles drawn from the
    /// markers stream. A room with fewer interior tiles than the combined spawn+loot draw places as many
    /// markers as fit rather than looping forever.</summary>
    internal static List<DungeonMarker> PlanMarkers(
        DungeonConfig config,
        DeterministicRng markers,
        IReadOnlyList<DungeonRoom> rooms)
    {
        var result = new List<DungeonMarker>();

        DungeonRoom? entrance = null;
        DungeonRoom? boss = null;
        foreach (DungeonRoom room in rooms)
        {
            if (room.RoomType == DungeonRoomType.Entrance)
            {
                entrance = room;
            }
            else if (room.RoomType == DungeonRoomType.Boss)
            {
                boss = room;
            }
        }

        if (entrance is not null)
        {
            result.Add(new DungeonMarker
            {
                Type = DungeonMarkerType.Entrance,
                Tile = RoomCenter(entrance),
                Tags = new List<string> { "entrance" },
            });
        }

        if (boss is not null)
        {
            result.Add(new DungeonMarker
            {
                Type = DungeonMarkerType.Objective,
                Tile = RoomCenter(boss),
                Tags = new List<string> { "boss" },
            });
        }

        foreach (DungeonRoom room in rooms)
        {
            if (room.RoomType == DungeonRoomType.Entrance)
            {
                continue;
            }

            int spawnCount = markers.Next(config.SpawnMarkersPerRoomMax + 1);
            int lootCount = markers.Next(config.LootMarkersPerRoomMax + 1);

            List<DungeonTile> picks = PickDistinctInteriorTiles(markers, room, spawnCount + lootCount);

            int index = 0;
            for (int i = 0; i < spawnCount && index < picks.Count; i++, index++)
            {
                result.Add(new DungeonMarker
                {
                    Type = DungeonMarkerType.Spawn,
                    Tile = picks[index],
                    Tags = new List<string> { "spawn" },
                });
            }

            bool treasureRoom = room.RoomType == DungeonRoomType.Key || room.RoomType == DungeonRoomType.Treasure;
            for (int i = 0; i < lootCount && index < picks.Count; i++, index++)
            {
                var tags = new List<string> { "loot" };
                if (treasureRoom)
                {
                    tags.Add("treasure");
                }

                result.Add(new DungeonMarker
                {
                    Type = DungeonMarkerType.Loot,
                    Tile = picks[index],
                    Tags = tags,
                });
            }
        }

        return result;
    }

    private static DungeonTile RoomCenter(DungeonRoom room)
    {
        return new DungeonTile(room.X + room.Width / 2, room.Z + room.Depth / 2, room.Floor);
    }

    /// <summary>Draws up to <paramref name="count"/> distinct interior tiles of <paramref name="room"/> without
    /// replacement, via a deterministic partial Fisher-Yates shuffle on the <paramref name="markers"/> stream.
    /// When the room's interior holds fewer tiles than requested, returns as many as fit rather than looping
    /// forever.</summary>
    private static List<DungeonTile> PickDistinctInteriorTiles(DeterministicRng markers, DungeonRoom room, int count)
    {
        var pool = new List<DungeonTile>(room.Width * room.Depth);
        for (int x = room.X; x < room.X + room.Width; x++)
        {
            for (int z = room.Z; z < room.Z + room.Depth; z++)
            {
                pool.Add(new DungeonTile(x, z, room.Floor));
            }
        }

        int take = Math.Min(count, pool.Count);
        var picks = new List<DungeonTile>(take);
        for (int i = 0; i < take; i++)
        {
            int j = i + markers.Next(pool.Count - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
            picks.Add(pool[i]);
        }

        return picks;
    }
}
