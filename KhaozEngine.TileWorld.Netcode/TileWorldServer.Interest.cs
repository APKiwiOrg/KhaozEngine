using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The footprint half of <see cref="TileWorldServer"/>'s area of interest: a viewer holds an entity when the
/// NEAREST tile of that entity's footprint is within <see cref="TileWorldServerConfig.InterestRadius"/> of the
/// viewer's own anchor tile. So an NxN body enters a snapshot on the tick its near edge crosses the radius rather
/// than up to N - 1 tiles later, when its ANCHOR does. See the other partials for construction
/// (<c>TileWorldServer.cs</c>), the tick order and the serve (<c>TileWorldServer.Tick.cs</c>) and the actors
/// (<c>TileWorldServer.Actors.cs</c>).
/// <para>Cell ownership and region handoff are UNTOUCHED and still measure from the anchor, which is the one
/// position <c>PositionOf</c> hands the host. Only the serve's read of the interest grid changed, in two steps. The
/// grid query is INFLATED by <see cref="LargestFootprintSize"/>, so every body whose near edge is inside the radius
/// is somewhere in the result whatever its anchor is doing, and the result is then POST FILTERED per viewer down to
/// the bodies that really do reach.</para>
/// <para>The metric is EUCLIDEAN in both steps, which is the one <c>InterestGrid.Query</c> has always used, and the
/// post filter runs the same <c>dx * dx + dz * dz &lt;= r * r</c> over the same floats, so a one-tile body is kept
/// or dropped exactly as the un-inflated query would have kept or dropped it.</para>
/// <para>The inflation is the DIAGONAL rather than the edge. An anchor is up to N - 1 tiles from the near tile on
/// each axis at once, which is <c>(N - 1) * sqrt(2)</c> apart Euclidean, and that worst case is reachable: a viewer
/// north-east of a large body measures to the body's north-east corner while the anchor sits the whole square
/// behind it. Inflating by N - 1 instead would leave such a body out of the query, where no post filter can put it
/// back.</para>
/// <para>A world of one-tile bodies pays NOTHING. The inflation is zero, the post filter never runs, and the served
/// set is the pre-footprint set value for value. The cost starts when a game spawns a body above one tile and it is
/// a wider grid sweep plus a predicate over the interest set: no extra walk of the cell's world, because the rects
/// the filter reads are collected by the one pass the plane filter already makes.</para>
/// </summary>
public sealed partial class TileWorldServer
{
    // One tile's diagonal, the Euclidean distance an anchor can sit behind its own near tile per tile of footprint.
    static readonly float TileDiagonal = MathF.Sqrt(2f);

    // The footprint of every interest member the current serve is filtering, collected by the plane filter's pass
    // over the home cell's world (see CollectPlane) and read by the predicate below. Written only while
    // collectFootprints is true, which cannot go back to false, so it can never be read stale.
    readonly Dictionary<long, TileRect> footprintByNetId = new();
    Predicate<long>? outsideFootprintInterest;
    bool collectFootprints;
    float viewerTileX;
    float viewerTileZ;

    /// <summary>The largest <see cref="TileMoveState.FootprintSize"/> this server has ever spawned, which is what
    /// the serve's interest query is inflated by: the radius grows by that body's diagonal, <c>(N - 1) * sqrt(2)</c>
    /// tiles. One until the first larger body is spawned, and it never decreases, so despawning the world's only cow
    /// leaves the query as wide as the cow made it. That costs a slightly wider grid sweep and cannot cost
    /// correctness, where a bound that shrank would be racing every serve.</summary>
    public int LargestFootprintSize { get; private set; } = 1;

    // What the serve asks the interest grid for, in tiles: the configured radius plus the diagonal of the largest
    // body in the world. Equal to TileWorldServerConfig.InterestRadius exactly while every body is one tile, which
    // is what keeps an all-one-tile world's served set identical to the pre-footprint one.
    float InterestQueryRadius => QueryRadiusFor(LargestFootprintSize);

    float QueryRadiusFor(int size) => config.InterestRadius + (size - 1) * TileDiagonal;

    // Taken at the SPAWN door rather than at the serve, and ahead of the write, for the reason the constructor
    // takes the plain InterestRadius against OverlapMargin: ShardHost.HomeInterest throws for a query wider than
    // the margin, and by then the throw is inside the tick and takes it down for every player on the server. A
    // body's anchor can be a whole inflated radius outside the viewer's home cell, so the cell has to hold it as a
    // ghost, and the margin is what decides how far the ghost band reaches.
    internal void ValidateFootprintFitsInterest(int size, string paramName)
    {
        if (size <= LargestFootprintSize) return;
        float radius = QueryRadiusFor(size);
        if (radius <= config.OverlapMargin) return;
        throw new ArgumentOutOfRangeException(paramName, size,
            $"A footprint of {size} needs an interest query of {radius} tiles (InterestRadius "
          + $"{config.InterestRadius} plus the body's diagonal), which exceeds OverlapMargin "
          + $"{config.OverlapMargin}: the home cell cannot hold the whole area of interest as ghosts. Raise "
          + "OverlapMargin to at least that query radius, or author a smaller body.");
    }

    // Raised only once the spawn is committed, so a placement or a cap refusal cannot leave the serve paying for a
    // body that never existed. Never lowered: see LargestFootprintSize.
    void NoteFootprintSize(int size)
    {
        if (size > LargestFootprintSize) LargestFootprintSize = size;
    }

    // Step two of the pair: drop everything the inflated query pulled in whose footprint does not actually reach the
    // viewer. A no-op while every body is one tile, where the query radius is the configured one and the grid has
    // already answered exactly this question.
    void FilterToFootprintInterest(HashSet<long> interest, long viewerNetId)
    {
        if (!collectFootprints) return;
        // A viewer is in its own interest set at distance zero, so the pass just collected its rect. Missing is the
        // defensive case the plane filter names, an unpositioned viewer, and it answers the same way: see
        // everything rather than nothing.
        if (!footprintByNetId.TryGetValue(viewerNetId, out TileRect viewer)) return;
        outsideFootprintInterest ??= IsOutsideFootprintInterest;
        // The viewer measures from its own ANCHOR, as it did before footprints. A player is one tile, so the rect's
        // corner IS its tile.
        viewerTileX = viewer.X;
        viewerTileZ = viewer.Z;
        interest.RemoveWhere(outsideFootprintInterest);
    }

    // The nearest tile of the rect to the viewer, clamped per axis, then the grid's own distance test over it. An
    // entity the pass could not measure is KEPT, the same way the plane filter keeps one it could not place.
    bool IsOutsideFootprintInterest(long netId)
    {
        if (!footprintByNetId.TryGetValue(netId, out TileRect rect)) return false;
        float nearestX = rect.X, nearestZ = rect.Z;
        if (!rect.IsEmpty)
        {
            nearestX = Math.Clamp(viewerTileX, rect.X, rect.X1 - 1);
            nearestZ = Math.Clamp(viewerTileZ, rect.Z, rect.Z1 - 1);
        }
        float dx = nearestX - viewerTileX, dz = nearestZ - viewerTileZ;
        float radius = config.InterestRadius;
        return dx * dx + dz * dz > radius * radius;
    }
}
