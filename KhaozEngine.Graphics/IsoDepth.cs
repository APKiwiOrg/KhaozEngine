using System;

namespace KhaozEngine.Graphics;

/// <summary>
/// A Y-sort key for isometric draw order. Sorting a draw list ascending by this key paints
/// far tiles before near ones so near sprites overlap far ones correctly. Primary order is
/// <see cref="Depth"/> (<c>wx + wy + zTier</c>); <see cref="Layer"/> breaks ties between things
/// that share a depth (e.g. ground decal under a unit on the same tile). Build one per drawable
/// via <see cref="IsoDepth.DepthKey"/>.
/// </summary>
public readonly struct IsoDepthKey : IComparable<IsoDepthKey>, IEquatable<IsoDepthKey>
{
    /// <summary>Primary sort value: <c>wx + wy</c> plus the z contribution.</summary>
    public readonly float Depth;

    /// <summary>Integer tiebreak when two keys share a <see cref="Depth"/>. Higher draws later (on top).</summary>
    public readonly int Layer;

    /// <summary>Creates a key from an explicit depth and layer. Prefer <see cref="IsoDepth.DepthKey"/>.</summary>
    public IsoDepthKey(float depth, int layer)
    {
        Depth = depth;
        Layer = layer;
    }

    /// <summary>Orders by <see cref="Depth"/> ascending, then <see cref="Layer"/> ascending.</summary>
    public int CompareTo(IsoDepthKey other)
    {
        int byDepth = Depth.CompareTo(other.Depth);
        return byDepth != 0 ? byDepth : Layer.CompareTo(other.Layer);
    }

    /// <inheritdoc/>
    public bool Equals(IsoDepthKey other) => Depth.Equals(other.Depth) && Layer == other.Layer;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IsoDepthKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Depth, Layer);

    public static bool operator ==(IsoDepthKey left, IsoDepthKey right) => left.Equals(right);
    public static bool operator !=(IsoDepthKey left, IsoDepthKey right) => !left.Equals(right);
    public static bool operator <(IsoDepthKey left, IsoDepthKey right) => left.CompareTo(right) < 0;
    public static bool operator >(IsoDepthKey left, IsoDepthKey right) => left.CompareTo(right) > 0;
    public static bool operator <=(IsoDepthKey left, IsoDepthKey right) => left.CompareTo(right) <= 0;
    public static bool operator >=(IsoDepthKey left, IsoDepthKey right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// Builds <see cref="IsoDepthKey"/> values for Y-sorting an isometric draw list. Render-only:
/// the consumer owns the draw list and sorts it; this just produces a stable, comparable key.
/// </summary>
public static class IsoDepth
{
    /// <summary>
    /// Depth key for a drawable at world <c>(wx, wy, z)</c> on integer <paramref name="layer"/>.
    /// Primary order is <c>wx + wy + z</c> (a higher tile or a higher stack draws later, i.e. in
    /// front); <paramref name="layer"/> is the tiebreak for things sharing the same depth. Sort the
    /// draw list ascending by the returned key.
    /// </summary>
    /// <param name="wx">World X.</param>
    /// <param name="wy">World Y.</param>
    /// <param name="z">Height. Contributes <c>z * <paramref name="zWeight"/></c> to the depth.</param>
    /// <param name="layer">Integer tiebreak at equal depth (higher draws on top).</param>
    /// <param name="zWeight">
    /// How strongly height pushes a drawable toward the front. Defaults to 1 (one z-unit counts as
    /// one tile-step of depth). Raise it so a tall stack reliably sorts in front of taller-but-nearer
    /// neighbours; set 0 to ignore height in ordering entirely.
    /// </param>
    public static IsoDepthKey DepthKey(float wx, float wy, float z = 0f, int layer = 0, float zWeight = 1f)
        => new((wx + wy) + z * zWeight, layer);
}
