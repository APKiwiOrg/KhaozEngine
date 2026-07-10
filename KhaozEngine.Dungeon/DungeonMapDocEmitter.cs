using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Bakes a <see cref="DungeonLayout"/> into an existing <see cref="MapDocument"/>: one <see cref="MapPlacement"/>
/// per floor/wall/door/stair piece, one <see cref="MapRegion"/> per room, markers as <see cref="MapSpawn"/>
/// (Spawn) or disc <see cref="MapRegion"/> (Loot/Objective/Entrance), a covering <c>FlattenFeatureDoc</c>, and an
/// expanded <see cref="MapBounds"/>. Always appends: never clears or replaces anything already in the target
/// document, so a document can accumulate several dungeon bakes (or dungeon content alongside hand-authored
/// content) side by side.
/// </summary>
public static class DungeonMapDocEmitter
{
    /// <summary>Emits every piece, region, marker, terrain feature, and bounds expansion for
    /// <paramref name="layout"/> into <paramref name="target"/>, resolving kit ids through
    /// <paramref name="kit"/> and world positions through <paramref name="plot"/>. Placement/spawn/marker-region
    /// ids follow <c>dungeon-&lt;bakeSalt8&gt;-&lt;n&gt;</c> and room regions
    /// <c>dungeon-room-&lt;bakeSalt8&gt;-&lt;id&gt;</c>, where bakeSalt8 is an FNV-1a 64 fold of
    /// <see cref="DungeonLayout.LayoutHash"/> with the plot's origin/baseY/yaw raw float bits: unique per
    /// (layout, plot) pair, so several bakes (different layouts, or the same layout at different plots) can
    /// accumulate in one document without id collisions. n is monotonic across every id one call mints, and
    /// every placement/spawn/marker-region carries the "dungeon" tag. Every emitted <see cref="MapSpawn"/> gets
    /// <paramref name="spawnArchetypeId"/> as its <see cref="MapSpawn.ArchetypeId"/> (default
    /// <c>"dungeon-spawn"</c>, a placeholder the game maps or replaces), so a baked document always satisfies
    /// the validator's non-empty-archetype rule and saves cleanly.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/>, <paramref name="kit"/>, or
    /// <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="spawnArchetypeId"/> is null, empty, or
    /// whitespace.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="kit"/> has no mapping for a
    /// <see cref="DungeonPiece"/> the layout needs. The message names the missing piece
    /// (see <see cref="DungeonKitMap.Require"/>).</exception>
    public static void Emit(DungeonLayout layout, DungeonKitMap kit, DungeonPlotTransform plot, MapDocument target,
        string spawnArchetypeId = "dungeon-spawn")
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(kit);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(spawnArchetypeId);

        string bakeSalt8 = BakeSalt8(layout, plot);
        int counter = 0;

        float cellSize = layout.CellSizeMeters;
        float floorHeight = layout.FloorHeightMeters;

        // The cell-to-piece mapping (which piece goes where, and which way it faces) is shared with
        // DungeonStamp via PieceMapper, so the two sinks can never drift apart on placement/prop counts.
        Dictionary<DungeonTile, (int Dx, int Dz)> passageDirection = PieceMapper.BuildPassageDirections(layout);

        EmitCells(layout, kit, plot, target, passageDirection, cellSize, floorHeight, bakeSalt8, ref counter);
        EmitStairRuns(layout, kit, plot, target, cellSize, floorHeight, bakeSalt8, ref counter);
        EmitRooms(layout, plot, target, cellSize, bakeSalt8);
        EmitMarkers(layout, plot, target, spawnArchetypeId, cellSize, floorHeight, bakeSalt8, ref counter);
        EmitTerrainAndBounds(layout, plot, target, cellSize);
    }

    // The per-bake id salt: the layout hash folded with the plot's placement (origin, baseY, yaw), so ids
    // are unique per (layout, plot) pair. A bare layout hash collides in two accumulating-bake scenarios:
    // two DIFFERENT layouts share the 0-based room ids the room-region names carry, and the SAME layout
    // baked at two plots repeats every id verbatim. FNV-1a 64 with the same constants and float folding
    // (raw bit pattern, never GetHashCode) as DungeonLayout.LayoutHash, so the salt stays cross-platform
    // stable and deterministic for identical inputs.
    private static string BakeSalt8(DungeonLayout layout, DungeonPlotTransform plot)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        ulong hash = fnvOffsetBasis;
        hash = MixUInt64(hash, fnvPrime, layout.LayoutHash());
        hash = MixUInt32(hash, fnvPrime, BitConverter.SingleToUInt32Bits(plot.OriginX));
        hash = MixUInt32(hash, fnvPrime, BitConverter.SingleToUInt32Bits(plot.OriginZ));
        hash = MixUInt32(hash, fnvPrime, BitConverter.SingleToUInt32Bits(plot.BaseY));
        hash = MixUInt32(hash, fnvPrime, BitConverter.SingleToUInt32Bits(plot.YawRadians));

        return hash.ToString("x16").Substring(0, 8);
    }

    private static ulong MixUInt64(ulong hash, ulong prime, ulong value)
    {
        hash = MixUInt32(hash, prime, (uint)value);
        hash = MixUInt32(hash, prime, (uint)(value >> 32));
        return hash;
    }

    private static ulong MixUInt32(ulong hash, ulong prime, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= prime;
        }

        return hash;
    }

    private static void EmitCells(
        DungeonLayout layout,
        DungeonKitMap kit,
        DungeonPlotTransform plot,
        MapDocument target,
        Dictionary<DungeonTile, (int Dx, int Dz)> passageDirection,
        float cellSize,
        float floorHeight,
        string bakeSalt8,
        ref int counter)
    {
        foreach (PieceMapper.CellPiece cellPiece in PieceMapper.EnumerateCellPieces(layout, passageDirection))
        {
            float yaw = PieceMapper.LocalYaw(cellPiece.Dx, cellPiece.Dz) - plot.YawRadians;
            AddPlacement(target, kit, cellPiece.Piece, plot, cellPiece.Tile, cellSize, floorHeight, yaw, bakeSalt8,
                ref counter);
        }
    }

    private static void EmitStairRuns(
        DungeonLayout layout,
        DungeonKitMap kit,
        DungeonPlotTransform plot,
        MapDocument target,
        float cellSize,
        float floorHeight,
        string bakeSalt8,
        ref int counter)
    {
        foreach (PieceMapper.StairRun run in PieceMapper.EnumerateStairRuns(layout))
        {
            (float lx, float ly, float lz) = plot.TileCenter(run.Lower, cellSize, floorHeight);
            (float ux, float uy, float uz) = plot.TileCenter(run.Upper, cellSize, floorHeight);

            float yaw = PieceMapper.LocalYaw(run.Dx, run.Dz) - plot.YawRadians;

            target.Placements.Add(new MapPlacement
            {
                Id = NextId(bakeSalt8, ref counter),
                Kind = kit.Require(DungeonPiece.StairUp),
                X = (lx + ux) * 0.5f,
                Y = (ly + uy) * 0.5f,
                Z = (lz + uz) * 0.5f,
                Yaw = yaw,
                Tags = new List<string> { "dungeon" },
            });
        }
    }

    private static void EmitRooms(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        MapDocument target,
        float cellSize,
        string bakeSalt8)
    {
        foreach (DungeonRoom room in layout.Rooms)
        {
            target.Regions.Add(new MapRegion
            {
                Name = $"dungeon-room-{bakeSalt8}-{room.Id}",
                Shape = new PolygonShapeDoc { Points = RoomCorners(plot, room, cellSize) },
                Tags = new List<string> { "dungeon", "room", room.RoomType.ToString().ToLowerInvariant() },
            });
        }
    }

    private static void EmitMarkers(
        DungeonLayout layout,
        DungeonPlotTransform plot,
        MapDocument target,
        string spawnArchetypeId,
        float cellSize,
        float floorHeight,
        string bakeSalt8,
        ref int counter)
    {
        foreach (DungeonMarker marker in layout.Markers)
        {
            (float x, _, float z) = plot.TileCenter(marker.Tile, cellSize, floorHeight);
            var tags = new List<string>(marker.Tags) { "dungeon", $"floor:{marker.Tile.Floor}" };

            if (marker.Type == DungeonMarkerType.Spawn)
            {
                target.Spawns.Add(new MapSpawn
                {
                    Id = NextId(bakeSalt8, ref counter),
                    ArchetypeId = spawnArchetypeId,
                    X = x,
                    Z = z,
                    Tags = tags,
                });
            }
            else
            {
                target.Regions.Add(new MapRegion
                {
                    Name = NextId(bakeSalt8, ref counter),
                    Shape = new DiscShapeDoc { CenterX = x, CenterZ = z, Radius = cellSize * 0.5f },
                    Tags = tags,
                });
            }
        }
    }

    private static void EmitTerrainAndBounds(DungeonLayout layout, DungeonPlotTransform plot, MapDocument target, float cellSize)
    {
        float plotWidth = layout.Width * cellSize;
        float plotDepth = layout.Depth * cellSize;

        (float x0, float z0) = PieceMapper.TransformXZ(plot, 0f, 0f);
        (float x1, float z1) = PieceMapper.TransformXZ(plot, plotWidth, 0f);
        (float x2, float z2) = PieceMapper.TransformXZ(plot, plotWidth, plotDepth);
        (float x3, float z3) = PieceMapper.TransformXZ(plot, 0f, plotDepth);

        float minX = MathF.Min(MathF.Min(x0, x1), MathF.Min(x2, x3));
        float maxX = MathF.Max(MathF.Max(x0, x1), MathF.Max(x2, x3));
        float minZ = MathF.Min(MathF.Min(z0, z1), MathF.Min(z2, z3));
        float maxZ = MathF.Max(MathF.Max(z0, z1), MathF.Max(z2, z3));

        target.Bounds.MinX = MathF.Min(target.Bounds.MinX, minX);
        target.Bounds.MinZ = MathF.Min(target.Bounds.MinZ, minZ);
        target.Bounds.MaxX = MathF.Max(target.Bounds.MaxX, maxX);
        target.Bounds.MaxZ = MathF.Max(target.Bounds.MaxZ, maxZ);

        (float centerX, float centerZ) = PieceMapper.TransformXZ(plot, plotWidth * 0.5f, plotDepth * 0.5f);
        float radius = 0.5f * MathF.Sqrt(plotWidth * plotWidth + plotDepth * plotDepth);

        target.Terrain.Features.Add(new FlattenFeatureDoc
        {
            CenterX = centerX,
            CenterZ = centerZ,
            Radius = radius,
            TargetHeight = plot.BaseY,
        });
    }

    private static void AddPlacement(
        MapDocument target,
        DungeonKitMap kit,
        DungeonPiece piece,
        DungeonPlotTransform plot,
        DungeonTile tile,
        float cellSize,
        float floorHeight,
        float yaw,
        string bakeSalt8,
        ref int counter)
    {
        (float x, float y, float z) = plot.TileCenter(tile, cellSize, floorHeight);
        target.Placements.Add(new MapPlacement
        {
            Id = NextId(bakeSalt8, ref counter),
            Kind = kit.Require(piece),
            X = x,
            Y = y,
            Z = z,
            Yaw = yaw,
            Tags = new List<string> { "dungeon" },
        });
    }

    private static List<float[]> RoomCorners(DungeonPlotTransform plot, DungeonRoom room, float cellSize)
    {
        float minX = room.X * cellSize;
        float minZ = room.Z * cellSize;
        float maxX = (room.X + room.Width) * cellSize;
        float maxZ = (room.Z + room.Depth) * cellSize;

        var points = new List<float[]>(4);
        AddCorner(plot, points, minX, minZ);
        AddCorner(plot, points, maxX, minZ);
        AddCorner(plot, points, maxX, maxZ);
        AddCorner(plot, points, minX, maxZ);
        return points;
    }

    private static void AddCorner(DungeonPlotTransform plot, List<float[]> points, float localX, float localZ)
    {
        (float x, float z) = PieceMapper.TransformXZ(plot, localX, localZ);
        points.Add(new[] { x, z });
    }

    private static string NextId(string bakeSalt8, ref int counter)
    {
        string id = $"dungeon-{bakeSalt8}-{counter}";
        counter++;
        return id;
    }
}
