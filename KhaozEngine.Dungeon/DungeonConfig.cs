using System;

namespace KhaozEngine.Dungeon;

/// <summary>
/// Whether a stamped/baked dungeon is left open-top or given roofed interiors. A pure sink-time geometry
/// choice: it changes only the ceiling pieces the two sinks (<see cref="DungeonMapDocEmitter"/> and
/// <see cref="DungeonStamp"/>) emit, never the generated layout structure (rooms, corridors, gating), so it
/// is excluded from <see cref="DungeonLayout.LayoutHash"/> and does not affect determinism.
/// </summary>
public enum DungeonCeilingMode
{
    /// <summary>No ceilings: the dungeon reads as roofless ruins seen from above. The default, and the
    /// original behaviour before roofed interiors were added.</summary>
    Open,

    /// <summary>A ceiling over every walkable cell AND over each stair shaft's <see cref="DungeonCellKind.StairVoid"/>
    /// headroom cutout (except where a walkable cell or another StairVoid sits directly above, whose own slab is the
    /// roof), so the dungeon reads as an enclosed cave. The stair shaft is roofed at the UPPER floor's ceiling height
    /// (the StairVoids sit on the upper floor above the treads), so it is capped overhead rather than open to the
    /// sky, while the treads stay clear at head height for the climb.</summary>
    Roofed
}

/// <summary>
/// Tunables for <c>DungeonGenerator.Generate</c>: plot and room sizing, floor count, the critical-path and
/// loop-edge targets, lock/key count, and marker density caps. Call <see cref="Validate"/> before generating.
/// It throws <see cref="ArgumentException"/> naming the first offending property.
/// </summary>
public sealed class DungeonConfig
{
    /// <summary>World size of one tile edge, in metres.</summary>
    public float CellSizeMeters { get; set; } = 2f;

    /// <summary>World height between floors, in metres.</summary>
    public float FloorHeightMeters { get; set; } = 4f;

    /// <summary>Number of rooms the generator aims to place.</summary>
    public int RoomCountTarget { get; set; } = 12;

    /// <summary>Smallest allowed room interior span, in tiles.</summary>
    public int RoomMinTiles { get; set; } = 3;

    /// <summary>Largest allowed room interior span, in tiles.</summary>
    public int RoomMaxTiles { get; set; } = 8;

    /// <summary>Number of vertical floors the layout may use.</summary>
    public int MaxFloors { get; set; } = 1;

    /// <summary>Smallest corridor width, in tiles. Default 1. A per-corridor width is drawn from the growth RNG
    /// only when <see cref="CorridorMaxWidth"/> exceeds this value; when they are equal every corridor is exactly
    /// this wide and no randomness is consumed, so <c>min == max == 1</c> reproduces the pre-width layouts
    /// byte-for-byte. The drawn width is capped at generation time to the narrower of the source room edge and the
    /// destination room edge it spans, so a wide draw still places against a smaller room by narrowing.</summary>
    public int CorridorMinWidth { get; set; } = 1;

    /// <summary>Largest corridor width, in tiles. Default 1 (single-file corridors, exact back-compat). Raise it
    /// above <see cref="CorridorMinWidth"/> for grand, multi-tile connector halls.</summary>
    public int CorridorMaxWidth { get; set; } = 1;

    /// <summary>Percent chance in [0, 100] that a grown room is an elongated <see cref="DungeonRoomType.Hall"/>
    /// grand connector instead of a normal room. Default 0: no halls, and no randomness is consumed (byte-compat).
    /// When positive, the plot must fit a hall (see <see cref="HallMaxLengthTiles"/>) on both axes.</summary>
    public int HallChancePercent { get; set; } = 0;

    /// <summary>Smallest hall long-axis span, in tiles. Only consulted when <see cref="HallChancePercent"/> is
    /// positive. A hall's long axis runs along the corridor that reached it; its short axis (girth) is a normal
    /// room span in <see cref="RoomMinTiles"/>..<see cref="RoomMaxTiles"/>.</summary>
    public int HallMinLengthTiles { get; set; } = 10;

    /// <summary>Largest hall long-axis span, in tiles. Only consulted when <see cref="HallChancePercent"/> is
    /// positive. When halls are enabled the plot must be at least this plus two on both axes (a hall can run in
    /// either orientation).</summary>
    public int HallMaxLengthTiles { get; set; } = 16;

    /// <summary>Plot width, in tiles.</summary>
    public int PlotWidthTiles { get; set; } = 64;

    /// <summary>Plot depth, in tiles.</summary>
    public int PlotDepthTiles { get; set; } = 64;

    /// <summary>Advisory and reserved: validated but not yet consumed by generation. The boss room is
    /// currently derived as the farthest room from the entrance by BFS edge-distance (see
    /// <see cref="LayoutStats.CriticalPathLength"/> for the realized length), regardless of this value. The
    /// knob is reserved for a future growth-heuristics/grammar layer that steers room placement toward a
    /// target critical-path length.</summary>
    public int CriticalPathTarget { get; set; } = 6;

    /// <summary>Extra non-critical edges to add as loops, beyond the critical path.</summary>
    public int LoopEdgeBudget { get; set; } = 2;

    /// <summary>Number of lock/key pairs to place along the critical path.</summary>
    public int LockCount { get; set; } = 1;

    /// <summary>Whether the layout must include a boss room at the end of the critical path.</summary>
    public bool BossRoom { get; set; } = true;

    /// <summary>Maximum spawn markers placed per room.</summary>
    public int SpawnMarkersPerRoomMax { get; set; } = 3;

    /// <summary>Maximum loot markers placed per room.</summary>
    public int LootMarkersPerRoomMax { get; set; } = 1;

    /// <summary>Whether the sinks roof the dungeon (<see cref="DungeonCeilingMode.Roofed"/>) or leave it
    /// open-top (<see cref="DungeonCeilingMode.Open"/>, the default). A pure sink-time geometry choice that
    /// never changes the generated layout structure.</summary>
    public DungeonCeilingMode CeilingMode { get; set; } = DungeonCeilingMode.Open;

    /// <summary>Height of the ceiling above each floor, in metres, when <see cref="CeilingMode"/> is
    /// <see cref="DungeonCeilingMode.Roofed"/>. Null (the default) resolves to <see cref="FloorHeightMeters"/>,
    /// so a roofed floor's ceiling sits flush with the floor above. Ignored in <see cref="DungeonCeilingMode.Open"/>.</summary>
    public float? CeilingHeightMeters { get; set; }

    /// <summary>Throws <see cref="ArgumentException"/> naming the first invalid property, in check order:
    /// positive cell/floor sizes, RoomMinTiles between 1 and RoomMaxTiles inclusive, positive room count, floor
    /// count and critical-path target, non-negative loop budget, lock count and marker maxima, corridor widths
    /// (at least 1, min not exceeding max), hall knobs (chance in 0..100, min length at least 1 not exceeding
    /// max), a positive ceiling height when one is set, a plot large enough to fit the largest room plus a
    /// one-tile margin on each side, and (only when halls are enabled) large enough to fit the largest hall on
    /// both axes.</summary>
    public void Validate()
    {
        if (CellSizeMeters <= 0f)
        {
            throw new ArgumentException("CellSizeMeters must be greater than zero.", nameof(CellSizeMeters));
        }

        if (FloorHeightMeters <= 0f)
        {
            throw new ArgumentException("FloorHeightMeters must be greater than zero.", nameof(FloorHeightMeters));
        }

        if (RoomMinTiles < 1)
        {
            throw new ArgumentException("RoomMinTiles must be at least 1.", nameof(RoomMinTiles));
        }

        if (RoomMinTiles > RoomMaxTiles)
        {
            throw new ArgumentException("RoomMinTiles must not exceed RoomMaxTiles.", nameof(RoomMinTiles));
        }

        if (RoomCountTarget < 1)
        {
            throw new ArgumentException("RoomCountTarget must be at least 1.", nameof(RoomCountTarget));
        }

        if (MaxFloors < 1)
        {
            throw new ArgumentException("MaxFloors must be at least 1.", nameof(MaxFloors));
        }

        if (CriticalPathTarget < 1)
        {
            throw new ArgumentException("CriticalPathTarget must be at least 1.", nameof(CriticalPathTarget));
        }

        if (LoopEdgeBudget < 0)
        {
            throw new ArgumentException("LoopEdgeBudget must not be negative.", nameof(LoopEdgeBudget));
        }

        if (LockCount < 0)
        {
            throw new ArgumentException("LockCount must not be negative.", nameof(LockCount));
        }

        if (SpawnMarkersPerRoomMax < 0)
        {
            throw new ArgumentException("SpawnMarkersPerRoomMax must not be negative.", nameof(SpawnMarkersPerRoomMax));
        }

        if (LootMarkersPerRoomMax < 0)
        {
            throw new ArgumentException("LootMarkersPerRoomMax must not be negative.", nameof(LootMarkersPerRoomMax));
        }

        if (CorridorMinWidth < 1)
        {
            throw new ArgumentException("CorridorMinWidth must be at least 1.", nameof(CorridorMinWidth));
        }

        if (CorridorMinWidth > CorridorMaxWidth)
        {
            throw new ArgumentException("CorridorMinWidth must not exceed CorridorMaxWidth.", nameof(CorridorMinWidth));
        }

        if (HallChancePercent < 0 || HallChancePercent > 100)
        {
            throw new ArgumentException("HallChancePercent must be between 0 and 100.", nameof(HallChancePercent));
        }

        if (HallMinLengthTiles < 1)
        {
            throw new ArgumentException("HallMinLengthTiles must be at least 1.", nameof(HallMinLengthTiles));
        }

        if (HallMinLengthTiles > HallMaxLengthTiles)
        {
            throw new ArgumentException("HallMinLengthTiles must not exceed HallMaxLengthTiles.", nameof(HallMinLengthTiles));
        }

        if (CeilingHeightMeters is <= 0f)
        {
            throw new ArgumentException("CeilingHeightMeters must be greater than zero when set.", nameof(CeilingHeightMeters));
        }

        if (PlotWidthTiles < RoomMaxTiles + 2)
        {
            throw new ArgumentException("PlotWidthTiles must be at least RoomMaxTiles + 2.", nameof(PlotWidthTiles));
        }

        if (PlotDepthTiles < RoomMaxTiles + 2)
        {
            throw new ArgumentException("PlotDepthTiles must be at least RoomMaxTiles + 2.", nameof(PlotDepthTiles));
        }

        // Only enforced when halls are enabled: a hall's long axis (up to HallMaxLengthTiles) can run in either
        // orientation, so both plot dimensions must fit it plus a one-tile margin on each side. Gated on
        // HallChancePercent so a halls-off config with a tight plot stays valid.
        if (HallChancePercent > 0 && PlotWidthTiles < HallMaxLengthTiles + 2)
        {
            throw new ArgumentException(
                "PlotWidthTiles must be at least HallMaxLengthTiles + 2 when HallChancePercent is positive.", nameof(PlotWidthTiles));
        }

        if (HallChancePercent > 0 && PlotDepthTiles < HallMaxLengthTiles + 2)
        {
            throw new ArgumentException(
                "PlotDepthTiles must be at least HallMaxLengthTiles + 2 when HallChancePercent is positive.", nameof(PlotDepthTiles));
        }
    }
}
