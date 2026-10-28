namespace KhaozEngine.Terrain;

/// <summary>Per-placement visibility for retained cluster generations. Implementations must retain metadata
/// for both source generations reported by PropClusterDrawState. Indices follow the original build order.</summary>
public interface IPropClusterVisibility
{
    /// <summary>Whether any retained cluster needs visibility-aware submission. True by default, so older
    /// adapters refuse unsupported visibility instead of exposing hidden geometry.</summary>
    bool RequiresVisibility => true;

    /// <summary>Whether this cluster needs visibility-aware colour and shadow submission.</summary>
    bool Controls(PropClusterKey key);

    /// <summary>Number of placements in the requested source generation.</summary>
    int PlacementCount(PropClusterKey key, long generation);

    /// <summary>Whether one placement is withheld from colour but retains its shadow.</summary>
    bool IsHidden(PropClusterKey key, long generation, int placementIndex);

    /// <summary>How far one placement's colour is faded out, from 0 (drawn in full) to 1 (withheld like
    /// <see cref="IsHidden"/>). Between the two the colour dissolves while the shadow stays whole. A placement
    /// <see cref="IsHidden"/> reports hidden stays hidden whatever this says. A fade forces a mixed merge back to
    /// resident individuals, and is ignored for a merge that has none rather than blanking the whole merge.</summary>
    float ColourFade(PropClusterKey key, long generation, int placementIndex) => 0f;
}

/// <summary>Submitted placement totals. A merged handle counts its represented placements once.</summary>
public readonly record struct PropClusterDrawCounts(int Visible, int ShadowOnly)
{
    public static PropClusterDrawCounts operator +(PropClusterDrawCounts a, PropClusterDrawCounts b) =>
        new(a.Visible + b.Visible, a.ShadowOnly + b.ShadowOnly);
}
