using System;
using KhaozEngine.Dungeon.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Deterministic entry point for dungeon generation. <see cref="Generate"/> validates the config, grows a
/// tree of rooms joined by corridors (and, when the config allows more than one floor, by upward stairs), runs
/// the wall pass, assembles a completable-by-construction <see cref="DungeonLayout"/>, and re-proves that
/// completability via <see cref="DungeonSolver.Verify"/> before returning it. Identical config and seed always
/// produce an identical layout (see <see cref="DungeonLayout.LayoutHash"/>). Gating and markers arrive in later
/// tasks. For now the layout carries empty keys and markers.
/// </summary>
public static class DungeonGenerator
{
    /// <summary>Generates a dungeon layout for <paramref name="config"/> seeded by <paramref name="seed"/>.
    /// Grows same-floor rooms and corridors, committing each room together with its corridor, doors, and edge
    /// atomically, then applies the wall pass. Never throws for a tight plot: it places what fits and reports
    /// <see cref="LayoutStats.Saturated"/>. Before returning, runs <see cref="DungeonSolver.Verify"/> on the
    /// assembled layout and throws if it is not solvable, then fills
    /// <see cref="LayoutStats.CriticalPathLength"/> via <see cref="DungeonSolver"/>'s room-graph BFS.</summary>
    /// <param name="config">Generation tunables. Validated first via <see cref="DungeonConfig.Validate"/>.</param>
    /// <param name="seed">Seed for the deterministic RNG streams.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="config"/> fails validation.</exception>
    /// <exception cref="InvalidOperationException">The assembled layout failed
    /// <see cref="DungeonSolver.Verify"/>. The message includes every reported error.</exception>
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
        LoopPlanner.PlanLoopEdges(config, rooms, grown);
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
                FloorsUsed = RoomGrower.CountFloorsUsed(grown.Cells, width, depth, floors),
                LocksRequested = config.LockCount,
                LocksPlaced = 0,
                Saturated = grown.Saturated,
            },
        };

        Array.Copy(grown.Cells, layout.CellsMutable, grown.Cells.Length);

        DungeonSolveReport report = DungeonSolver.Verify(layout);
        if (!report.IsSolvable)
        {
            throw new InvalidOperationException(
                "Generated dungeon layout is not solvable: " + string.Join(" ", report.Errors));
        }

        layout.Stats.CriticalPathLength = DungeonSolver.ComputeCriticalPathLength(layout);

        return layout;
    }
}
