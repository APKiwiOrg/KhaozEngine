using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>A bounded goal whose pure predicate tests candidate feet positions on actual surfaces.</summary>
public sealed class NavGoalRegion
{
    readonly Func<Vector3, bool> _contains;

    /// <summary>World anchor used only to bound the search estimate, not as a snapped goal.</summary>
    public Vector3 Anchor { get; }

    /// <summary>Conservative horizontal radius around <see cref="Anchor"/> containing every member.</summary>
    public float HorizontalExtent { get; }

    /// <summary>
    /// Creates a region with a finite anchor and finite nonnegative extent. The predicate must be
    /// pure throughout each query and include only feet positions within the horizontal bound.
    /// </summary>
    public NavGoalRegion(Vector3 anchor, float horizontalExtent, Func<Vector3, bool> contains)
    {
        ArgumentNullException.ThrowIfNull(contains);
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(anchor.Z))
            throw new ArgumentOutOfRangeException(nameof(anchor));
        if (!float.IsFinite(horizontalExtent) || horizontalExtent < 0f)
            throw new ArgumentOutOfRangeException(nameof(horizontalExtent));
        Anchor = anchor;
        HorizontalExtent = horizontalExtent;
        _contains = contains;
    }

    /// <summary>Tests membership using the caller's exact candidate feet position.</summary>
    public bool Contains(Vector3 feetPosition) => _contains(feetPosition);
}
