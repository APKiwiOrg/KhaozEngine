using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>Colour and shadow routing for one retained cluster, separate from build and lifetime ownership.</summary>
internal sealed class PropClusterVisibilityDraw
{
    readonly List<PropPlacement> _visible = new();
    readonly List<PropPlacement> _hidden = new();

    public PropClusterDrawCounts Draw(IPropClusterRenderBackend backend, PropClusterKey key, PropLayer layer,
        IReadOnlyList<PropPlacement> placements, int representedCount, MeshHandle? handle,
        PropClusterDrawState state, Vector3 focus, IPropClusterVisibility visibility)
    {
        if (!state.DrawsIndividuals && !state.DrawsMerged) return default;
        PropClusterVisibilityDecision decision = PropClusterVisibilityDecision.Resolve(key, state, visibility);
        state = decision.State;
        int mergedCount = decision.Controlled ? decision.MergedPlacementCount : representedCount;

        PropClusterDrawCounts individual = state.DrawsIndividuals
            ? DrawIndividuals(backend, key, layer, placements, focus, decision, visibility) : default;
        PropClusterDrawCounts merged = default;
        if (state.DrawsMerged && handle is { } mesh)
        {
            if (decision.DrawsMergedColour)
            {
                backend.DrawMerged(mesh, layer, state.MergedDissolve, state.MergedComplement);
                merged = new(mergedCount, 0);
            }
            else if (layer.CastsShadows)
            {
                backend.DrawShadowOnlyMerged(mesh, layer, state.MergedDissolve, state.MergedComplement);
                merged = new(0, mergedCount);
            }
        }
        // Complementary individual and merged draws represent the same placements during a transition.
        return new(Math.Max(individual.Visible, merged.Visible), Math.Max(individual.ShadowOnly, merged.ShadowOnly));
    }

    PropClusterDrawCounts DrawIndividuals(IPropClusterRenderBackend backend, PropClusterKey key, PropLayer layer,
        IReadOnlyList<PropPlacement> placements, Vector3 focus, PropClusterVisibilityDecision decision,
        IPropClusterVisibility visibility)
    {
        float dissolveFloor = decision.State.IndividualDissolveFloor;
        if (!decision.Controlled) return new(backend.DrawProps(placements, layer, focus, dissolveFloor), 0);
        _visible.Clear();
        _hidden.Clear();
        for (int i = 0; i < placements.Count; i++)
        {
            if (decision.DrawsIndividualColour(key, i, visibility)) _visible.Add(placements[i]);
            else _hidden.Add(placements[i]);
        }
        int visible = _visible.Count == 0 ? 0 : backend.DrawProps(_visible, layer, focus, dissolveFloor);
        int hidden = !layer.CastsShadows || _hidden.Count == 0 ? 0
            : backend.DrawShadowOnlyProps(_hidden, layer, focus, dissolveFloor);
        return new(visible, hidden);
    }
}
