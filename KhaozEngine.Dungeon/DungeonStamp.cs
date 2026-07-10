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
/// contiguous walkable-floor run (excluding the stair tread cells, which are covered by their own pitched
/// ramp box instead), one oriented ramp per stair run, and (when the layout is
/// <see cref="DungeonCeilingMode.Roofed"/>) one per contiguous ceiling run. Render-free and
/// physics-backend-free: callers turn props into actual scene content and register statics with whatever
/// <see cref="IPhysicsWorld"/> they run.
/// </summary>
public static class DungeonStamp
{
    /// <summary>Half the 0.2m nominal slab/ramp thickness shared by floor slabs and stair ramps.</summary>
    private const float ThinHalfThickness = 0.1f;

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
        BuildStairRamps(layout, plot, cellSize, floorHeight, statics);
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

    /// <summary>Walkable cells that get a floor slab: every walkable kind except the two stair-tread cells
    /// (<see cref="DungeonCellKind.StairLower"/>/<see cref="DungeonCellKind.StairUpper"/>), which sit under
    /// the pitched stair ramp box (<see cref="BuildStairRamps"/>) instead of a flat slab.</summary>
    private static bool IsFloorSlabCell(DungeonCellKind kind)
    {
        return DungeonLayout.IsWalkable(kind)
            && kind != DungeonCellKind.StairLower
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

    /// <summary>One oriented box per stair run, spanning the full <c>2*cellSize</c> horizontal run (the
    /// <see cref="DungeonCellKind.StairLower"/> cell through the <see cref="DungeonCellKind.StairUpper"/>
    /// cell) and the full <paramref name="floorHeight"/> rise to the upper floor's landing. The box's local
    /// +Z is the run's length axis (matching the greybox stair piece's own "climbs local +Z" convention), so
    /// its orientation composes the same yaw <see cref="PieceMapper.LocalYaw"/> gives the stair prop with an
    /// additional pitch of <c>atan2(floorHeight, runMeters)</c> about the local X (width) axis, applied
    /// BEFORE the yaw (pitch in the piece's own frame, then yaw to aim that frame at the run direction).
    /// Positioned so the box's top face runs from the lower cell's floor to the upper floor's landing: the
    /// un-thinned top-surface line's horizontal midpoint sits exactly halfway between the lower and upper
    /// tile centers (by symmetry of the run's 2-cell span) at the average of the two floors' Y, then the box
    /// center is that point offset by half the thickness along the box's own (rotated) local -Y axis.</summary>
    private static void BuildStairRamps(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        float cellSize,
        float floorHeight,
        List<(PhysicsShape Shape, Pose Pose)> statics)
    {
        float runMeters = 2f * cellSize;
        float length = MathF.Sqrt(runMeters * runMeters + floorHeight * floorHeight);
        float pitch = MathF.Atan2(floorHeight, runMeters);

        foreach (PieceMapper.StairRun run in PieceMapper.EnumerateStairRuns(layout))
        {
            (float lx, float ly, float lz) = plot.TileCenter(run.Lower, cellSize, floorHeight);
            (float ux, _, float uz) = plot.TileCenter(run.Upper, cellSize, floorHeight);

            float yaw = PieceMapper.LocalYaw(run.Dx, run.Dz) - plot.YawRadians;
            Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch);

            Vector3 worldUpAxis = Vector3.Transform(Vector3.UnitY, orientation);

            var topMidpoint = new Vector3((lx + ux) * 0.5f, ly + floorHeight * 0.5f, (lz + uz) * 0.5f);
            Vector3 center = topMidpoint - ThinHalfThickness * worldUpAxis;

            var halfExtents = new Vector3(cellSize * 0.5f, ThinHalfThickness, length * 0.5f);
            statics.Add((new BoxShape(halfExtents), new Pose(center, orientation)));
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
