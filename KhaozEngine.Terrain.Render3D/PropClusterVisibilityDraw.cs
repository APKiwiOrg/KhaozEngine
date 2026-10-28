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
    readonly PropPlacement[] _single = new PropPlacement[1];

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
        int faded = 0;
        for (int i = 0; i < placements.Count; i++)
        {
            float fade = decision.DrawsIndividualColour(key, i, visibility)
                ? decision.IndividualColourFade(key, i, visibility) : 1f;
            if (fade <= 0f) _visible.Add(placements[i]);
            else if (fade >= 1f) _hidden.Add(placements[i]);
            else faded += DrawFaded(backend, layer, placements[i], focus, fade, dissolveFloor);
        }
        int visible = faded + (_visible.Count == 0 ? 0 : backend.DrawProps(_visible, layer, focus, dissolveFloor));
        int hidden = !layer.CastsShadows || _hidden.Count == 0 ? 0
            : backend.DrawShadowOnlyProps(_hidden, layer, focus, dissolveFloor);
        return new(visible, hidden);
    }

    // A partly faded placement: its colour dissolves by the fade, and its shadow is submitted on its own at the
    // ordinary floor, because the dissolve threshold also discards in the shadow pass and would thin the shadow.
    int DrawFaded(IPropClusterRenderBackend backend, PropLayer layer, PropPlacement placement, Vector3 focus,
        float fade, float dissolveFloor)
    {
        _single[0] = placement;
        int drawn = backend.DrawColourOnlyProps(_single, layer, focus, MathF.Max(fade, dissolveFloor));
        if (layer.CastsShadows) backend.DrawShadowOnlyProps(_single, layer, focus, dissolveFloor);
        return drawn;
    }
}
