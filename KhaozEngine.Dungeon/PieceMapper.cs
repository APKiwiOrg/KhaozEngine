using System;
using System.Collections.Generic;

namespace KhaozEngine.Dungeon;

/// <summary>
/// The cell-to-piece mapping, plot-yaw math, and stair-run pairing shared by every sink that turns a
/// <see cref="DungeonLayout"/> into concrete content (<see cref="DungeonMapDocEmitter"/> bakes it into a
/// <see cref="KhaozEngine.MapDoc.MapDocument"/>, <see cref="DungeonStamp"/> stamps it into runtime props and
/// physics statics). Both sinks resolve the SAME set of pieces at the SAME tiles with the SAME yaw
/// convention by calling into this class rather than re-deriving any of it, so their outputs never drift
/// apart. Internal: this is plumbing between the two sinks, not part of the package's public surface.
/// </summary>
internal static class PieceMapper
{
    /// <summary>One piece placement resolved from a single raster cell: <paramref name="Piece"/> at
    /// <paramref name="Tile"/>, facing the plot-local direction (<paramref name="Dx"/>, <paramref name="Dz"/>)
    /// (zero for symmetric pieces). Feed the direction into <see cref="LocalYaw"/> to get the piece-local yaw
    /// component, then subtract the plot yaw per <see cref="LocalYaw"/>'s composition rule.
    /// <paramref name="YOffset"/> is added to the resolved world Y: a small negative <see cref="FloorPieceYOffset"/>
    /// for floor-level pieces (<see cref="DungeonPiece.Floor"/> and the <see cref="DungeonPiece.StairDown"/> landing)
    /// so their TOP - not their base - lands on the floor collision slab, and the ceiling height for a
    /// <see cref="DungeonPiece.Ceiling"/> so it sits above the floor rather than on it.</summary>
    internal readonly record struct CellPiece(DungeonTile Tile, DungeonPiece Piece, int Dx, int Dz, float YOffset = 0f);

    /// <summary>One stair run: the two END treads, <see cref="DungeonCellKind.StairLower"/> and
    /// <see cref="DungeonCellKind.StairUpper"/> (both on the lower floor, the first and second-to-last cells of
    /// <c>CommitStair</c>'s <c>[StairLower, StairMid, StairUpper, StairTop]</c> path ordering, so the run spans
    /// the full three-tread ramp) plus the plot-local direction from one to the other.</summary>
    internal readonly record struct StairRun(DungeonTile Lower, DungeonTile Upper, int Dx, int Dz);

    /// <summary>The Y shift (metres) applied to a rendered floor-level piece (<see cref="DungeonPiece.Floor"/> and
    /// the <see cref="DungeonPiece.StairDown"/> landing) so its TOP - not its base - lands at the floor's Y, flush
    /// with the top of the <see cref="DungeonStamp"/> floor collision slab the capsule actually rests on. The
    /// greybox floor/landing kit meshes are authored one collision-slab-thickness tall with their base at local
    /// y=0 (<c>PropLoader</c> drops every mesh base to 0), so without this drop the visible floor would float one
    /// slab thickness ABOVE the collision, reading as a character sunk into the floor. The greybox floor/landing
    /// kit thickness EQUALS the collision slab thickness (<c>2 * DungeonStamp.ThinHalfThickness</c>) by design, so
    /// dropping the piece by exactly that thickness lands its top on floorY. Both sinks (<see cref="DungeonStamp"/>
    /// props and <see cref="DungeonMapDocEmitter"/> baked placements) apply it, so runtime and baked content match.</summary>
    internal const float FloorPieceYOffset = -2f * DungeonStamp.ThinHalfThickness;

    /// <summary>Every door-frame (or stair-top landing) cell belongs to exactly one edge, on one end or the
    /// other of that edge's <see cref="DungeonEdge.Doors"/> pair. Both ends share the same horizontal passage
    /// direction (the edge is a straight axis-aligned run), so one dictionary built from
    /// <c>Doors[0]-&gt;Doors[1]</c> covers both cells for every corridor, stair, and loop edge.</summary>
    internal static Dictionary<DungeonTile, (int Dx, int Dz)> BuildPassageDirections(DungeonLayout layout)
    {
        var passageDirection = new Dictionary<DungeonTile, (int Dx, int Dz)>();
        foreach (DungeonEdge edge in layout.Edges)
        {
            if (edge.Doors.Count < 2)
            {
                continue;
            }

            (int dx, int dz) = UnitDirection(edge.Doors[0], edge.Doors[1]);
            passageDirection[edge.Doors[0]] = (dx, dz);
            passageDirection[edge.Doors[1]] = (dx, dz);
        }

        return passageDirection;
    }

    /// <summary>Walks every cell in <paramref name="layout"/>'s raster (floor, then Z, then X, matching the
    /// raster's own storage order) and yields one <see cref="CellPiece"/> per piece a sink must place there:
    /// a <see cref="DungeonPiece.Floor"/> for walkable room/corridor/door cells, a
    /// <see cref="DungeonPiece.DoorFrame"/> additionally for door cells (facing <paramref name="passageDirection"/>,
    /// built by <see cref="BuildPassageDirections"/>), a <see cref="DungeonPiece.Wall"/> for wall cells, and a
    /// <see cref="DungeonPiece.StairDown"/> for the upper-floor stair landing. <see cref="DungeonCellKind.StairLower"/>/
    /// <see cref="DungeonCellKind.StairUpper"/>/<see cref="DungeonCellKind.StairVoid"/>/<see cref="DungeonCellKind.Empty"/>
    /// yield nothing here: the stair run itself is covered once per run by <see cref="EnumerateStairRuns"/>.</summary>
    internal static IEnumerable<CellPiece> EnumerateCellPieces(
        DungeonLayout layout,
        IReadOnlyDictionary<DungeonTile, (int Dx, int Dz)> passageDirection)
    {
        for (int f = 0; f < layout.Floors; f++)
        {
            for (int z = 0; z < layout.Depth; z++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    var tile = new DungeonTile(x, z, f);
                    switch (layout.GetCell(x, z, f))
                    {
                        case DungeonCellKind.RoomFloor:
                        case DungeonCellKind.Corridor:
                            yield return new CellPiece(tile, DungeonPiece.Floor, 0, 0, FloorPieceYOffset);
                            break;

                        case DungeonCellKind.DoorFrame:
                            yield return new CellPiece(tile, DungeonPiece.Floor, 0, 0, FloorPieceYOffset);
                            (int ddx, int ddz) = passageDirection.TryGetValue(tile, out (int Dx, int Dz) dir) ? dir : (0, 0);
                            yield return new CellPiece(tile, DungeonPiece.DoorFrame, ddx, ddz);
                            break;

                        case DungeonCellKind.Wall:
                            yield return new CellPiece(tile, DungeonPiece.Wall, 0, 0);
                            break;

                        case DungeonCellKind.StairTop:
                            (int sdx, int sdz) = passageDirection.TryGetValue(tile, out (int Dx, int Dz) sdir) ? sdir : (0, 0);
                            yield return new CellPiece(tile, DungeonPiece.StairDown, sdx, sdz, FloorPieceYOffset);
                            break;

                        // StairLower/StairMid/StairUpper: covered once per run by EnumerateStairRuns.
                        // StairVoid/Empty: nothing.
                    }

                    // A ceiling roofs every walkable cell (whatever piece it resolved to above, including the
                    // stair-tread cells that yield nothing here) when the layout is roofed and no walkable cell
                    // sits directly above it. Emitted at YOffset = ceiling height so the sink lifts it off the
                    // floor. Shared with DungeonStamp's ceiling slabs via HasCeiling, so both sinks agree.
                    if (HasCeiling(layout, x, z, f))
                    {
                        yield return new CellPiece(tile, DungeonPiece.Ceiling, 0, 0, layout.CeilingHeightMeters);
                    }
                }
            }
        }
    }

    /// <summary>Yields one <see cref="StairRun"/> per <see cref="DungeonEdgeKind.Stair"/> edge, in
    /// <see cref="DungeonLayout.Edges"/> order (so the nth stair run pairs with the nth stair edge for any
    /// caller that also filters <c>layout.Edges</c> the same way).</summary>
    internal static IEnumerable<StairRun> EnumerateStairRuns(DungeonLayout layout)
    {
        foreach (DungeonEdge edge in layout.Edges)
        {
            if (edge.Kind != DungeonEdgeKind.Stair)
            {
                continue;
            }

            // CommitStair always orders Path as [StairLower, StairMid, StairUpper, StairTop]; the ramp spans the
            // two END treads (Path[0] to Path[^2] = StairUpper), so it covers the full three-tread run.
            DungeonTile lower = edge.Path[0];
            DungeonTile upper = edge.Path[^2];
            (int dx, int dz) = UnitDirection(lower, upper);

            yield return new StairRun(lower, upper, dx, dz);
        }
    }

    /// <summary>Whether the cell at (<paramref name="x"/>, <paramref name="z"/>, <paramref name="f"/>) gets a
    /// ceiling: true when <paramref name="layout"/> is <see cref="DungeonCeilingMode.Roofed"/>, the cell is
    /// walkable, and the cell directly above it (floor <c>f + 1</c>, same XZ) is neither walkable nor
    /// <see cref="DungeonCellKind.StairVoid"/>. The walkable-above exemption keeps stacked floors from
    /// double-roofing: where the floor above already has a walkable cell, that floor's own slab is the roof, so a
    /// second ceiling would only z-fight. The <see cref="DungeonCellKind.StairVoid"/> exemption keeps the whole
    /// stair shaft open: a StairVoid sits directly above every tread (<see cref="DungeonCellKind.StairLower"/>,
    /// <see cref="DungeonCellKind.StairMid"/>, <see cref="DungeonCellKind.StairUpper"/>) as the deliberately-open
    /// headroom the ramp climbs through, so those treads must stay uncapped even though the void above them is not
    /// itself walkable. Both sinks call this so their ceiling pieces and ceiling collision slabs cover exactly the
    /// same cells.</summary>
    internal static bool HasCeiling(DungeonLayout layout, int x, int z, int f)
    {
        if (layout.CeilingMode != DungeonCeilingMode.Roofed || !DungeonLayout.IsWalkable(layout.GetCell(x, z, f)))
        {
            return false;
        }

        DungeonCellKind above = layout.GetCell(x, z, f + 1);
        return !DungeonLayout.IsWalkable(above) && above != DungeonCellKind.StairVoid;
    }

    /// <summary>The plot-local yaw (radians, plot yaw NOT composed) that rotates local +Z onto the unit
    /// direction (<paramref name="dx"/>, <paramref name="dz"/>), matching the engine-wide
    /// <c>Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)</c> convention consumers apply (e.g.
    /// <c>ChunkStatics</c>, <c>DungeonStamp</c>): local +Z maps to (sin yaw, cos yaw), so +Z rotates towards
    /// +X as yaw increases. Zero direction (symmetric pieces) yields zero.
    ///
    /// Plot-yaw composition rule: <see cref="DungeonPlotTransform.TileCenter"/> rotates POSITIONS with
    /// x' = x cos - z sin, z' = x sin + z cos, which maps a local direction at quaternion-yaw theta to the
    /// world direction (sin(theta - yaw), cos(theta - yaw)), the OPPOSITE XZ handedness to the quaternion
    /// convention. So the plot yaw enters every placement's final yaw NEGATED:
    /// worldYaw = LocalYaw(dx, dz) - plot.YawRadians (and bare -plot.YawRadians for symmetric pieces, since
    /// LocalYaw(0, 0) is zero), verified by
    /// <c>DungeonMapDocEmitterTests.Emit_DirectionalYaw_FacesWorldDirection_UnderPlotYaw</c>. Static collision
    /// poses (<see cref="DungeonStamp"/>) use the same convention for their Y-axis orientation component.</summary>
    internal static float LocalYaw(int dx, int dz)
    {
        return MathF.Atan2(dx, dz);
    }

    /// <summary>The axis-aligned unit step (one of (+-1, 0) or (0, +-1)) from <paramref name="from"/> to
    /// <paramref name="to"/>'s X/Z, ignoring floor. Every edge endpoint pair used here is a straight run, so
    /// exactly one component is non-zero.</summary>
    internal static (int Dx, int Dz) UnitDirection(DungeonTile from, DungeonTile to)
    {
        return (Math.Sign(to.X - from.X), Math.Sign(to.Z - from.Z));
    }

    /// <summary>Rotates the plot-local point (<paramref name="localX"/>, <paramref name="localZ"/>) by
    /// <see cref="DungeonPlotTransform.YawRadians"/> and offsets it to world space: the same position rotation
    /// as <see cref="DungeonPlotTransform.TileCenter"/>, generalized to an arbitrary local point (e.g. a wall
    /// or floor-slab run's center, which is not itself a single tile's center).</summary>
    internal static (float X, float Z) TransformXZ(DungeonPlotTransform plot, float localX, float localZ)
    {
        float cos = MathF.Cos(plot.YawRadians);
        float sin = MathF.Sin(plot.YawRadians);

        float x = localX * cos - localZ * sin + plot.OriginX;
        float z = localX * sin + localZ * cos + plot.OriginZ;
        return (x, z);
    }
}
