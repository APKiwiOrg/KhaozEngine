using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Dungeon;

/// <summary>One runtime prop instance: a kit content id at a world position/orientation, resolved the same
/// way as an emitted <c>MapPlacement</c> (see <see cref="DungeonMapDocEmitter"/>) but without a
/// <c>MapDocument</c> in between. <see cref="Yaw"/> follows the engine-wide
/// <c>Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw)</c> convention. <see cref="Scale"/> is always 1
/// today: <see cref="DungeonStamp"/> never scales pieces, it is carried so callers have a uniform place to
/// apply their own per-instance variation later without an API break.</summary>
public readonly record struct DungeonPropInstance(string KitId, float X, float Y, float Z, float Yaw, float Scale);

/// <summary>The runtime output of <see cref="DungeonStamp.Build"/>: every prop instance plus every merged
/// static collision shape/pose pair, ready to hand to a scene and an <see cref="IPhysicsWorld"/>
/// respectively.</summary>
public sealed record DungeonStampResult(
    IReadOnlyList<DungeonPropInstance> Props,
    IReadOnlyList<(PhysicsShape Shape, Pose Pose)> Statics);

/// <summary>
/// Stamps a <see cref="DungeonLayout"/> into runtime content: <see cref="DungeonStampResult.Props"/> is one
/// <see cref="DungeonPropInstance"/> per piece the layout needs, IDENTICAL to what
/// <see cref="DungeonMapDocEmitter"/> would place (both sinks share the cell-to-piece mapping via
/// <see cref="PieceMapper"/>, so they can never drift apart). <see cref="DungeonStampResult.Statics"/> is a
/// small, merged set of axis-run <see cref="BoxShape"/> collision boxes: one per contiguous wall run, one per
/// contiguous walkable-floor run (excluding the stair tread cells, which are covered by their own solid step
/// boxes instead), a run of solid box steps per stair run (upright treads the character's step-up probe mounts),
/// and (when the layout is <see cref="DungeonCeilingMode.Roofed"/>) one per contiguous ceiling run. Render-free and
/// physics-backend-free: callers turn props into actual scene content and register statics with whatever
/// <see cref="IPhysicsWorld"/> they run.
/// </summary>
public static class DungeonStamp
{
    /// <summary>Half the 0.2m nominal slab/ramp thickness shared by floor slabs and stair ramps. Internal (not
    /// private) so <see cref="PieceMapper.FloorPieceYOffset"/> can drop a rendered floor piece by the full slab
    /// thickness (<c>2 * ThinHalfThickness</c>) to land its top flush on the collision slab.</summary>
    internal const float ThinHalfThickness = 0.1f;

    /// <summary>Builds every prop instance and static collision shape for <paramref name="layout"/>, resolving
    /// kit ids through <paramref name="kit"/> and world positions/orientations through <paramref name="plot"/>.
    /// See the type doc for what each list contains.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> or <paramref name="kit"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="kit"/> has no mapping for a
    /// <see cref="DungeonPiece"/> the layout needs. The message names the missing piece
    /// (see <see cref="DungeonKitMap.Require"/>).</exception>
    public static DungeonStampResult Build(DungeonLayout layout, DungeonKitMap kit, DungeonPlotTransform plot)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(kit);

        float cellSize = layout.CellSizeMeters;
        float floorHeight = layout.FloorHeightMeters;

        Dictionary<DungeonTile, (int Dx, int Dz)> passageDirection = PieceMapper.BuildPassageDirections(layout);

        var props = new List<DungeonPropInstance>();
        BuildProps(layout, kit, plot, passageDirection, cellSize, floorHeight, props);

        var statics = new List<(PhysicsShape Shape, Pose Pose)>();
        BuildWalls(layout, plot, cellSize, floorHeight, statics);
        BuildFloorSlabs(layout, plot, cellSize, floorHeight, statics);
        BuildStairSteps(layout, plot, cellSize, floorHeight, statics);
        BuildCeilingSlabs(layout, plot, cellSize, floorHeight, statics);

        return new DungeonStampResult(props, statics);
    }

    private static void BuildProps(
        DungeonLayout layout,
        DungeonKitMap kit,
        DungeonPlotTransform plot,
        Dictionary<DungeonTile, (int Dx, int Dz)> passageDirection,
        float cellSize,
        float floorHeight,
        List<DungeonPropInstance> props)
    {
        foreach (PieceMapper.CellPiece cellPiece in PieceMapper.EnumerateCellPieces(layout, passageDirection))
        {
            (float x, float y, float z) = plot.TileCenter(cellPiece.Tile, cellSize, floorHeight);
            float yaw = PieceMapper.LocalYaw(cellPiece.Dx, cellPiece.Dz) - plot.YawRadians;
            props.Add(new DungeonPropInstance(kit.Require(cellPiece.Piece), x, y + cellPiece.YOffset, z, yaw, 1f));
        }

        foreach (PieceMapper.StairRun run in PieceMapper.EnumerateStairRuns(layout))
        {
            (float lx, float ly, float lz) = plot.TileCenter(run.Lower, cellSize, floorHeight);
            (float ux, float uy, float uz) = plot.TileCenter(run.Upper, cellSize, floorHeight);
            float yaw = PieceMapper.LocalYaw(run.Dx, run.Dz) - plot.YawRadians;

            props.Add(new DungeonPropInstance(
                kit.Require(DungeonPiece.StairUp),
                (lx + ux) * 0.5f,
                (ly + uy) * 0.5f,
                (lz + uz) * 0.5f,
                yaw,
                1f));
        }
    }

    /// <summary>Per floor, per Z row: greedy-merges contiguous <see cref="DungeonCellKind.Wall"/> cells along
    /// X into one box. Orientation is the plot's axis-aligned yaw only (no pitch): the same
    /// <c>-plot.YawRadians</c> convention <see cref="PieceMapper.LocalYaw"/> assigns to symmetric pieces,
    /// since a wall run's local axes are dungeon-grid-aligned and only the plot rotation tips them.</summary>
    private static void BuildWalls(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        float cellSize,
        float floorHeight,
        List<(PhysicsShape Shape, Pose Pose)> statics)
    {
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -plot.YawRadians);

        for (int f = 0; f < layout.Floors; f++)
        {
            for (int z = 0; z < layout.Depth; z++)
            {
                ForEachRun(layout, f, z, x => layout.GetCell(x, z, f) == DungeonCellKind.Wall, (runStart, runLength) =>
                {
                    float localCenterX = (runStart + runLength * 0.5f) * cellSize;
                    float localCenterZ = (z + 0.5f) * cellSize;
                    (float worldX, float worldZ) = PieceMapper.TransformXZ(plot, localCenterX, localCenterZ);
                    float worldY = plot.BaseY + f * floorHeight + floorHeight * 0.5f;

                    var halfExtents = new Vector3(runLength * cellSize * 0.5f, floorHeight * 0.5f, cellSize * 0.5f);
                    var pose = new Pose(new Vector3(worldX, worldY, worldZ), orientation);
                    statics.Add((new BoxShape(halfExtents), pose));
                });
            }
        }
    }

    /// <summary>Per floor, per Z row: greedy-merges contiguous walkable, non-stair-tread cells along X into
    /// one thin (0.2m) box whose top face sits at the floor's Y. Same axis-aligned orientation rule as
    /// <see cref="BuildWalls"/>.</summary>
    private static void BuildFloorSlabs(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        float cellSize,
        float floorHeight,
        List<(PhysicsShape Shape, Pose Pose)> statics)
    {
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -plot.YawRadians);

        for (int f = 0; f < layout.Floors; f++)
        {
            for (int z = 0; z < layout.Depth; z++)
            {
                ForEachRun(layout, f, z, x => IsFloorSlabCell(layout.GetCell(x, z, f)), (runStart, runLength) =>
                {
                    float localCenterX = (runStart + runLength * 0.5f) * cellSize;
                    float localCenterZ = (z + 0.5f) * cellSize;
                    (float worldX, float worldZ) = PieceMapper.TransformXZ(plot, localCenterX, localCenterZ);
                    float floorY = plot.BaseY + f * floorHeight;
                    float worldY = floorY - ThinHalfThickness;

                    var halfExtents = new Vector3(runLength * cellSize * 0.5f, ThinHalfThickness, cellSize * 0.5f);
                    var pose = new Pose(new Vector3(worldX, worldY, worldZ), orientation);
                    statics.Add((new BoxShape(halfExtents), pose));
                });
            }
        }
    }

    /// <summary>Walkable cells that get a floor slab: every walkable kind except the three stair-tread cells
    /// (<see cref="DungeonCellKind.StairLower"/>/<see cref="DungeonCellKind.StairMid"/>/<see cref="DungeonCellKind.StairUpper"/>),
    /// which sit under the solid stair step boxes (<see cref="BuildStairSteps"/>) instead of a flat slab. The
    /// landing (<see cref="DungeonCellKind.StairTop"/>) is NOT a tread - it sits past the ramp's top edge - so it
    /// keeps its flat slab, the solid ground a climber emerges onto.</summary>
    private static bool IsFloorSlabCell(DungeonCellKind kind)
    {
        return DungeonLayout.IsWalkable(kind)
            && kind != DungeonCellKind.StairLower
            && kind != DungeonCellKind.StairMid
            && kind != DungeonCellKind.StairUpper;
    }

    /// <summary>Greedy-merges the contiguous run of X cells on (<paramref name="z"/>, <paramref name="f"/>)
    /// whose X index satisfies <paramref name="predicate"/>, invoking <paramref name="onRun"/> once per run
    /// with the run's start X and length. Cells failing the predicate simply break the run. The predicate is
    /// keyed by X (not by cell kind) so a run can depend on more than the cell itself, e.g. the ceiling run
    /// tests the cell above via <see cref="PieceMapper.HasCeiling"/>.</summary>
    private static void ForEachRun(DungeonLayout layout, int f, int z, Func<int, bool> predicate,
        Action<int, int> onRun)
    {
        int x = 0;
        while (x < layout.Width)
        {
            if (!predicate(x))
            {
                x++;
                continue;
            }

            int runStart = x;
            while (x < layout.Width && predicate(x))
            {
                x++;
            }

            onRun(runStart, x - runStart);
        }
    }

    /// <summary>The nominal maximum stair riser (metres). Each stair is collided (and drawn) as a run of solid
    /// upright box steps, each rising at most this much, kept BELOW the default <c>MoveTuning.StepHeight</c>
    /// (0.4 m) so the character's step-up probe mounts every tread. A single smooth PITCHED ramp box is NOT
    /// climbable from a flush floor in this engine: a capsule grounded at floor level cannot mount a
    /// walkable-slope prop that rises out of the floor (the prop-support sweep is deliberately gated off at floor
    /// level so a dome flank stays un-climbable), so the stair is discretised into step-up-mountable treads
    /// instead. Kept in sync with <c>tools/DungeonKitGen</c>, whose greybox stair mesh uses the same step-count
    /// formula so the visible steps coincide with the collision steps.</summary>
    private const float MaxStairRiserMeters = 0.34f;

    /// <summary>One run of solid box steps per stair, climbing the full <paramref name="floorHeight"/> over the
    /// three-tread run (from the lower cell's near edge to the upper cell's far edge). The step count is
    /// <c>ceil(floorHeight / <see cref="MaxStairRiserMeters"/>)</c>, so every riser stays under the default
    /// step-up height and the whole run is walkable by the character's step-up probe (see
    /// <see cref="MaxStairRiserMeters"/> for why a single pitched ramp box would not be). Each step box is UPRIGHT
    /// (yaw only, no pitch, so its up axis stays world-up like a wall/floor slab) and spans the cell width, the
    /// tread depth, and its own height (the lower floor up to that step's tread top); the boxes march along the
    /// run direction, matching the greybox stair mesh (same step count and geometry, so collision and visual
    /// coincide). The run length is derived from the two END treads (<see cref="PieceMapper.StairRun.Lower"/> and
    /// <see cref="PieceMapper.StairRun.Upper"/>, whose centres are two cells apart) plus one cell, so it covers
    /// all three tread cells and auto-adapts if the tread count ever changes. The top step reaches the upper
    /// floor, flush with the landing slab (<see cref="DungeonCellKind.StairTop"/>) one cell beyond, which a
    /// climber steps onto.</summary>
    private static void BuildStairSteps(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        float cellSize,
        float floorHeight,
        List<(PhysicsShape Shape, Pose Pose)> statics)
    {
        foreach (PieceMapper.StairRun run in PieceMapper.EnumerateStairRuns(layout))
        {
            (float lx, float ly, float lz) = plot.TileCenter(run.Lower, cellSize, floorHeight);
            (float ux, _, float uz) = plot.TileCenter(run.Upper, cellSize, floorHeight);

            // Run length = end-tread centre distance ((treads-1)*cellSize) + one cell, so the steps cover every
            // tread cell fully from the lower cell's near edge to the upper cell's far edge. The centre distance
            // is Euclidean so it stays correct under a rotated plot (a Manhattan |dx|+|dz| would overshoot on a
            // diagonal run and march the steps past the run footprint).
            float runMeters = MathF.Sqrt((ux - lx) * (ux - lx) + (uz - lz) * (uz - lz)) + cellSize;

            int steps = Math.Max(1, (int)MathF.Ceiling(floorHeight / MaxStairRiserMeters));
            float riser = floorHeight / steps;
            float depth = runMeters / steps;

            float yaw = PieceMapper.LocalYaw(run.Dx, run.Dz) - plot.YawRadians;
            Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

            // The run's horizontal centre (midpoint of the two end-tread centres) and the unit world XZ climb
            // direction (lower cell toward upper cell). Steps are laid out along it from the low end up.
            var runCenter = new Vector3((lx + ux) * 0.5f, ly, (lz + uz) * 0.5f);
            var lowerToUpper = new Vector3(ux - lx, 0f, uz - lz);
            Vector3 climbDir = lowerToUpper.LengthSquared() > 1e-9f ? Vector3.Normalize(lowerToUpper) : Vector3.UnitZ;

            for (int i = 0; i < steps; i++)
            {
                float treadTop = (i + 1) * riser;                          // this step's top, above the lower floor
                float zLocal = -runMeters * 0.5f + (i + 0.5f) * depth;     // step centre along the run from its centre
                Vector3 xz = runCenter + zLocal * climbDir;

                var halfExtents = new Vector3(cellSize * 0.5f, treadTop * 0.5f, depth * 0.5f);
                var center = new Vector3(xz.X, ly + treadTop * 0.5f, xz.Z);
                statics.Add((new BoxShape(halfExtents), new Pose(center, orientation)));
            }
        }
    }

    /// <summary>Per floor, per Z row: greedy-merges the contiguous run of ceiling cells (per
    /// <see cref="PieceMapper.HasCeiling"/>, the same predicate the prop sink uses, so slabs and ceiling props
    /// cover identical cells) along X into one thin (0.2m) box whose BOTTOM face sits at the ceiling underside
    /// (<c>floorY + ceilingHeight</c>), mirroring <see cref="BuildFloorSlabs"/> but lifted a ceiling height and
    /// facing down. Emits nothing in <see cref="DungeonCeilingMode.Open"/> (no cell satisfies the predicate).
    /// Same axis-aligned orientation rule as <see cref="BuildWalls"/>.</summary>
    private static void BuildCeilingSlabs(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        float cellSize,
        float floorHeight,
        List<(PhysicsShape Shape, Pose Pose)> statics)
    {
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -plot.YawRadians);
        float ceilingHeight = layout.CeilingHeightMeters;

        for (int f = 0; f < layout.Floors; f++)
        {
            for (int z = 0; z < layout.Depth; z++)
            {
                ForEachRun(layout, f, z, x => PieceMapper.HasCeiling(layout, x, z, f), (runStart, runLength) =>
                {
                    float localCenterX = (runStart + runLength * 0.5f) * cellSize;
                    float localCenterZ = (z + 0.5f) * cellSize;
                    (float worldX, float worldZ) = PieceMapper.TransformXZ(plot, localCenterX, localCenterZ);
                    float undersideY = plot.BaseY + f * floorHeight + ceilingHeight;
                    float worldY = undersideY + ThinHalfThickness;

                    var halfExtents = new Vector3(runLength * cellSize * 0.5f, ThinHalfThickness, cellSize * 0.5f);
                    var pose = new Pose(new Vector3(worldX, worldY, worldZ), orientation);
                    statics.Add((new BoxShape(halfExtents), pose));
                });
            }
        }
    }
}
