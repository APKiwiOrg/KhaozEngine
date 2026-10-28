namespace KhaozEngine.Terrain;

/// <summary>The effective representations, generations and fades shared by colour, shadow and outline submission.</summary>
public readonly record struct PropClusterVisibilityDecision(
    PropClusterDrawState State, bool Controlled, int MergedPlacementCount, bool MergedShadowOnly)
{
    /// <summary>Whether the merged representation submits colour rather than only a shadow.</summary>
    public bool DrawsMergedColour => State.DrawsMerged && !MergedShadowOnly;

    /// <summary>Apply generation-aware visibility to the distance-based representation choices.</summary>
    public static PropClusterVisibilityDecision Resolve(PropClusterKey key, PropClusterDrawState state,
        IPropClusterVisibility visibility)
    {
        if (!state.DrawsIndividuals && !state.DrawsMerged) return new(state, false, 0, false);
        bool controlled = visibility.Controls(key);
        int mergedCount = controlled && state.DrawsMerged
            ? visibility.PlacementCount(key, state.MergedSourceGeneration) : 0;
        int mergedHidden = 0, mergedFaded = 0;
        for (int i = 0; i < mergedCount; i++)
        {
            if (visibility.IsHidden(key, state.MergedSourceGeneration, i)) mergedHidden++;
            else if (visibility.ColourFade(key, state.MergedSourceGeneration, i) > 0f) mergedFaded++;
        }

        // A merged colour draw cannot express per-placement visibility. Resident individuals replace a
        // mixed merge without the HLOD floor, including beyond the distance transition or after a failed build.
        // A colour fade never counts toward a shadow-only merge: without resident individuals it is ignored, so a
        // fade can never blank every other placement the merge carries.
        if ((mergedFaded > 0 || (mergedHidden > 0 && mergedHidden < mergedCount)) && state.HasIndividualGeometry)
            state = state with { DrawsIndividuals = true, IndividualDissolveFloor = 0f, DrawsMerged = false };

        return new(state, controlled, mergedCount, mergedHidden > 0);
    }

    /// <summary>How far this placement's individual colour is faded, from the effective source generation. Zero
    /// for an uncontrolled cluster.</summary>
    public float IndividualColourFade(PropClusterKey key, int placementIndex, IPropClusterVisibility visibility) =>
        Controlled ? visibility.ColourFade(key, State.IndividualSourceGeneration, placementIndex) : 0f;

    /// <summary>Whether this placement submits individual colour from the effective source generation.</summary>
    public bool DrawsIndividualColour(PropClusterKey key, int placementIndex, IPropClusterVisibility visibility) =>
        State.DrawsIndividuals && (!Controlled
            || !visibility.IsHidden(key, State.IndividualSourceGeneration, placementIndex));
}
