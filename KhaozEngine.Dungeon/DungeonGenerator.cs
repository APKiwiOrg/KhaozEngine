using System;
using KhaozEngine.Dungeon.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Deterministic entry point for dungeon generation. <see cref="Generate"/> validates the config, grows a
/// tree of rooms joined by corridors on floor 0, runs the wall pass, and assembles a completable-by-construction
/// <see cref="DungeonLayout"/>. Identical config and seed always produce an identical layout (see
/// <see cref="DungeonLayout.LayoutHash"/>). Loop edges, gating, markers, and the solver arrive in later tasks;
/// for now the layout carries empty keys and markers and a zero critical-path length.
/// </summary>
public static class DungeonGenerator
{
    /// <summary>Generates a dungeon layout for <paramref name="config"/> seeded by <paramref name="seed"/>.
    /// Grows same-floor rooms and corridors, committing each room together with its corridor, doors, and edge
    /// atomically, then applies the wall pass. Never throws for a tight plot: it places what fits and reports
    /// <see cref="LayoutStats.Saturated"/>.</summary>
    /// <param name="config">Generation tunables. Validated first via <see cref="DungeonConfig.Validate"/>.</param>
    /// <param name="seed">Seed for the deterministic RNG streams.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="config"/> fails validation.</exception>
    public static DungeonLayout Generate(DungeonConfig config, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        int width = config.PlotWidthTiles;
        int depth = config.PlotDepthTiles;
        int floors = config.MaxFloors;

        var root = new DeterministicRng(seed);
        DeterministicRng rooms = root.CreateDerived("rooms");

        GrowResult grown = RoomGrower.Grow(config, rooms);
        RoomGrower.ApplyWallPass(grown.Cells, width, depth, floors);

        var layout = new DungeonLayout(width, depth, floors, config.CellSizeMeters, config.FloorHeightMeters)
        {
            Rooms = grown.Rooms,
            Edges = grown.Edges,
            Keys = Array.Empty<DungeonKeyPlacement>(),
            Markers = Array.Empty<DungeonMarker>(),
            Stats = new LayoutStats
            {
                RoomsRequested = config.RoomCountTarget,
                RoomsPlaced = grown.Rooms.Count,
                CriticalPathLength = 0,
                FloorsUsed = 1,
                LocksRequested = config.LockCount,
                LocksPlaced = 0,
                Saturated = grown.Saturated,
            },
        };

        Array.Copy(grown.Cells, layout.CellsMutable, grown.Cells.Length);
        return layout;
    }
}
