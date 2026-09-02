using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// A walk in progress: the tiles AFTER the start in walk order, plus the index of the next one to step into.
/// A VALUE, so it rides inside <see cref="TileMoveState"/> and is replayed by client reconciliation rather than
/// mutated. The tile list is treated as immutable: nothing here writes to it, and the two heads build theirs from
/// the same deterministic <see cref="TilePathfinder.FindPath"/> call.
/// <para>Advancing is an INDEX move rather than a list edit, which is what lets reconciliation replay a pending
/// window cheaply: rewinding a route to an earlier tick is one integer, and no step ever allocates.</para>
/// <para>Equality compares the REMAINING tiles rather than the array reference, so a route rebuilt on the client
/// from its wire form equals the server's, which is what the prediction tests assert on. Two routes that have
/// walked different distances down different arrays are equal exactly when the walk still ahead of them matches,
/// because that walk is the only part either head will act on.</para>
/// </summary>
public readonly struct TileRoute : IEquatable<TileRoute>
{
    static readonly TileCoord[] Empty = Array.Empty<TileCoord>();

    readonly IReadOnlyList<TileCoord>? _tiles;

    /// <summary>The idle route: no tiles, nothing to step into. What a standing player carries, and what a route
    /// becomes once its last step commits.</summary>
    public static TileRoute None => new(Empty, 0);

    /// <summary>Wraps a walk order and the index of the next tile to step into. An index past the end is CLAMPED to
    /// the end rather than rejected, so a route that has already finished is spelled the same way as one that never
    /// started and both read as idle.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="tiles"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public TileRoute(IReadOnlyList<TileCoord> tiles, int index)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), index, "Route index must be >= 0.");
        _tiles = tiles;
        Index = Math.Min(index, tiles.Count);
    }

    /// <summary>The walk order after the start tile, the shape <see cref="TilePath.Tiles"/> already has. Never null,
    /// including on a defaulted struct: the backing field is nullable and a null one answers the empty list, so the
    /// non-null annotation is TRUE rather than documented around. A <c>default(TileRoute)</c> is reachable in normal
    /// use (an ECS column is zero filled, a failed lookup hands back a defaulted struct, a component decode populates
    /// one after the fact), and every one of those reads as an empty route instead of throwing.</summary>
    public IReadOnlyList<TileCoord> Tiles => _tiles ?? Empty;

    /// <summary>Index of the next tile to step into. Equal to <c>Tiles.Count</c> when the walk is done.</summary>
    public int Index { get; }

    /// <summary>True when there is nothing left to step into. The one check every caller makes before
    /// <see cref="Next"/>, and the reason a defaulted <see cref="TileRoute"/> is safe to hold.</summary>
    public bool IsIdle => Index >= Tiles.Count;

    /// <summary>Number of steps still to take. The length of the wire form, and the length equality compares.</summary>
    public int Remaining => IsIdle ? 0 : Tiles.Count - Index;

    /// <summary>The tile the NEXT step will enter. Throws rather than answering the start tile, because a
    /// silently plausible answer here would show up as a player sliding into a tile nobody routed them to.
    /// <para>NOT the tile a step in flight is entering: that is <see cref="TileMoveState.Tile"/>, because a step
    /// commits its tile and <see cref="Advanced"/>s the route on the tick it STARTS. An overlay highlighting the
    /// walk ahead therefore starts at <c>state.Tile</c> and continues through <see cref="Tiles"/> from
    /// <see cref="Index"/>, which is a connected path. Starting it here instead leaves a hole exactly where the
    /// body is walking.</para></summary>
    /// <exception cref="InvalidOperationException">The route is idle.</exception>
    public TileCoord Next => IsIdle
        ? throw new InvalidOperationException("An idle TileRoute has no next tile. Check IsIdle first.")
        : Tiles[Index];

    /// <summary>The last tile of the walk, whatever progress has been made down it. This is the DESTINATION, so it
    /// keeps answering after the walk is finished and is what an arrival check compares against.</summary>
    /// <exception cref="InvalidOperationException">The route has no tiles at all.</exception>
    public TileCoord End => Tiles.Count > 0
        ? Tiles[Tiles.Count - 1]
        : throw new InvalidOperationException("An empty TileRoute has no end tile.");

    /// <summary>This route with the index moved on by one step, which is how a committed step is recorded. Idle
    /// stays idle, so a simulator that advances one tick too many is a no-op rather than a fault.</summary>
    public TileRoute Advanced() => IsIdle ? this : new TileRoute(Tiles, Index + 1);

    /// <summary>The route a pathfinder result walks. An empty path is <see cref="None"/>, so a click on the tile the
    /// player already stands on produces a standing state rather than a zero length walk.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static TileRoute FromPath(TilePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Tiles.Count == 0 ? None : new TileRoute(path.Tiles, 0);
    }

    /// <summary>Rebuilds a route by walking <paramref name="steps"/> from <paramref name="from"/>, the inverse of
    /// <see cref="RemainingSteps"/>. This is the pair that makes the wire form cheap: one byte per step instead of
    /// nine, and no pathfinder run on the receiving head. The plane never changes mid route, so every rebuilt tile
    /// takes the plane of <paramref name="from"/>.</summary>
    public static TileRoute FromSteps(TileCoord from, ReadOnlySpan<TileDirection> steps)
    {
        if (steps.Length == 0) return None;
        var tiles = new TileCoord[steps.Length];
        TileCoord cur = from;
        for (int i = 0; i < steps.Length; i++)
        {
            (int dx, int dz) = TileDirections.Delta(steps[i]);
            cur = new TileCoord(cur.X + dx, cur.Z + dz, from.Plane);
            tiles[i] = cur;
        }
        return new TileRoute(tiles, 0);
    }

    /// <summary>The REMAINING walk as one step direction per tile, starting from <paramref name="from"/> (the tile
    /// the owner currently stands on). Empty when idle. Only the remaining walk is emitted because the tiles already
    /// behind the index are history no receiver can act on.</summary>
    /// <exception cref="ArgumentException">Two consecutive route tiles are not adjacent, which means the route did
    /// not come from the pathfinder.</exception>
    public TileDirection[] RemainingSteps(TileCoord from)
    {
        if (IsIdle) return Array.Empty<TileDirection>();
        var steps = new TileDirection[Remaining];
        TileCoord cur = from;
        for (int i = 0; i < steps.Length; i++)
        {
            TileCoord next = Tiles[Index + i];
            steps[i] = Direction(cur, next);
            cur = next;
        }
        return steps;
    }

    /// <summary>The single step direction from <paramref name="a"/> to an adjacent <paramref name="b"/>. Scans
    /// <see cref="TileDirections.All"/> in its fixed order, so the answer is the same on both heads.</summary>
    /// <exception cref="ArgumentException">The two tiles are not adjacent, so no single step joins them.</exception>
    public static TileDirection Direction(TileCoord a, TileCoord b)
    {
        int dx = b.X - a.X, dz = b.Z - a.Z;
        foreach (TileDirection d in TileDirections.All)
        {
            (int ddx, int ddz) = TileDirections.Delta(d);
            if (ddx == dx && ddz == dz) return d;
        }
        throw new ArgumentException($"{a} and {b} are not adjacent, so there is no single step between them.", nameof(b));
    }

    /// <summary>Compares the remaining tiles, not the backing array. See the type doc for why.
    /// <para>Two routes over the SAME list at the same index are answered on the references, which is the case a
    /// reconcile hits on every tick a route rides through untouched: a state carried forward holds the list its
    /// route was built with. It is a shortcut and never a second rule, since one list at one index is one remaining
    /// slice. It deliberately does not fire on the two spellings of an empty route (a defaulted struct's null field
    /// against <see cref="None"/>'s <c>Array.Empty</c>), which the length comparison below answers equal.</para>
    /// <para><see cref="Remaining"/> is a computed property, so it is read ONCE into a local rather than on every
    /// iteration of a loop that runs per player per tick.</para></summary>
    public bool Equals(TileRoute other)
    {
        if (ReferenceEquals(_tiles, other._tiles) && Index == other.Index) return true;
        int remaining = Remaining;
        if (remaining != other.Remaining) return false;
        IReadOnlyList<TileCoord> mine = Tiles, theirs = other.Tiles;
        for (int i = 0; i < remaining; i++)
            if (!mine[Index + i].Equals(theirs[other.Index + i])) return false;
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TileRoute r && Equals(r);

    /// <summary>Hashes the same remaining tiles <see cref="Equals(TileRoute)"/> compares, so the two agree.</summary>
    public override int GetHashCode()
    {
        int remaining = Remaining;                          // computed, so read once rather than per iteration
        IReadOnlyList<TileCoord> tiles = Tiles;
        var hash = new HashCode();
        hash.Add(remaining);
        for (int i = 0; i < remaining; i++) hash.Add(tiles[Index + i]);
        return hash.ToHashCode();
    }

    /// <summary>Equality operator over <see cref="Equals(TileRoute)"/>.</summary>
    public static bool operator ==(TileRoute a, TileRoute b) => a.Equals(b);

    /// <summary>Inequality operator over <see cref="Equals(TileRoute)"/>.</summary>
    public static bool operator !=(TileRoute a, TileRoute b) => !a.Equals(b);
}
