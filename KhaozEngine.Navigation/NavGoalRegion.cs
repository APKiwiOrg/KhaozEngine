using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>A bounded goal whose pure predicate tests candidate feet positions on actual surfaces.</summary>
public sealed class NavGoalRegion
{
    // A public delegate and an engine-owned predicate share one reference slot.
    readonly object _predicate;

    /// <summary>World anchor used only to bound the search estimate, not as a snapped goal.</summary>
    public Vector3 Anchor { get; }

    /// <summary>Half-extent on each world XZ axis of the square around <see cref="Anchor"/> containing every member.</summary>
    public float HorizontalExtent { get; }

    /// <summary>
    /// Creates a region with a finite anchor and finite nonnegative extent. The predicate must be
    /// pure throughout each query and include only feet positions within the horizontal bound.
    /// </summary>
    public NavGoalRegion(Vector3 anchor, float horizontalExtent, Func<Vector3, bool> contains)
        : this(anchor, horizontalExtent, (object)(contains ?? throw new ArgumentNullException(nameof(contains))))
    {
    }

    internal NavGoalRegion(NavGoalPredicate predicate, Vector3 anchor, float horizontalExtent)
        : this(anchor, horizontalExtent, predicate ?? throw new ArgumentNullException(nameof(predicate)))
    {
    }

    private NavGoalRegion(Vector3 anchor, float horizontalExtent, object predicate)
    {
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(anchor.Z))
            throw new ArgumentOutOfRangeException(nameof(anchor));
        if (!float.IsFinite(horizontalExtent) || horizontalExtent < 0f)
            throw new ArgumentOutOfRangeException(nameof(horizontalExtent));
        Anchor = anchor;
        HorizontalExtent = horizontalExtent;
        _predicate = predicate;
    }

    /// <summary>Tests membership using the caller's exact candidate feet position.</summary>
    public bool Contains(Vector3 feetPosition) => _predicate is Func<Vector3, bool> contains
        ? contains(feetPosition)
        : ((NavGoalPredicate)_predicate).Contains(feetPosition);
}
