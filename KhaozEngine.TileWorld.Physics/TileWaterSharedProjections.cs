using System;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using S = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.BasisState;
using H = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.Header;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Per-trace parent basis projections lent to that parent's faces under an exact key.</summary>
internal sealed class TileWaterSharedProjections
{
    internal const int Slots = 16;
    internal struct Row
    {
        internal bool Resolved;
        internal TileWaterPreparedQuery.Multipliers Lambda, Velocity;
        internal V Q, V;
    }
    internal readonly record struct Snapshot(int ReservedBytes, int Published, int Borrowed, int Duplicates,
        int Exhausted, int Lends, int Misses, int Nested, int Unsupported, int Bypassed, int Copies, int Fresh);

    internal static bool BypassForTests;

    internal Snapshot Last { get; private set; }
    internal int HeldOwnersForTests => 0;
    internal static int ReservedPayloadBytes => 0;

    internal void BeginTrace() { }
    internal void EndTrace() => Last = default;
    internal void Settle(ref TileWaterPreparedQuery prepared, int retainedOffset) { }

    internal TileWaterProjectionSource Lend(TileWaterConvexShape shape, in H key, ReadOnlySpan<byte> slab) => default;

    internal static void Copy(ReadOnlySpan<S> states, Span<Row> rows) { }
}

/// <summary>A face's read-only view of its parent's reached projections, valid until the face is settled.</summary>
internal readonly ref struct TileWaterProjectionSource
{
    readonly ReadOnlySpan<int> _map;

    internal TileWaterProjectionSource(ReadOnlySpan<int> map, ReadOnlySpan<TileWaterSharedProjections.Row> rows,
        ReadOnlySpan<S> retained, TileWaterSharedProjections? owner = null)
    {
        _map = map;
    }

    internal bool IsEmpty => _map.IsEmpty;

    internal bool TryCopy(int ordinal, ref S state) => false;
}
