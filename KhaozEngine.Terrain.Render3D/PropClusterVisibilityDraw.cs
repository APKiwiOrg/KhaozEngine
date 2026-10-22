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
        bool controlled = visibility.Controls(key);
        int mergedCount = controlled && state.DrawsMerged
            ? visibility.PlacementCount(key, state.MergedSourceGeneration) : representedCount;
        int mergedHidden = 0;
        if (controlled && state.DrawsMerged)
            for (int i = 0; i < mergedCount; i++)
                if (visibility.IsHidden(key, state.MergedSourceGeneration, i)) mergedHidden++;

        // A merged colour draw cannot express visibility per building. Drop the HLOD fade floor when the
        // individual batch replaces it, so the entire visible part survives even beyond the HLOD transition.
        bool mixed = mergedHidden > 0 && mergedHidden < mergedCount;
        if (mixed && placements.Count > 0)
            return DrawIndividuals(backend, key, layer, placements, state.IndividualSourceGeneration,
                focus, 0f, visibility, controlled);

        PropClusterDrawCounts individual = state.DrawsIndividuals
            ? DrawIndividuals(backend, key, layer, placements, state.IndividualSourceGeneration,
                focus, state.IndividualDissolveFloor, visibility, controlled) : default;
        PropClusterDrawCounts merged = default;
        if (state.DrawsMerged && handle is { } mesh)
        {
            if (mergedHidden == 0)
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
        IReadOnlyList<PropPlacement> placements, long generation, Vector3 focus, float dissolveFloor,
        IPropClusterVisibility visibility, bool controlled)
    {
        if (!controlled) return new(backend.DrawProps(placements, layer, focus, dissolveFloor), 0);
        _visible.Clear();
        _hidden.Clear();
        for (int i = 0; i < placements.Count; i++)
        {
            if (visibility.IsHidden(key, generation, i)) _hidden.Add(placements[i]);
            else _visible.Add(placements[i]);
        }
        int visible = _visible.Count == 0 ? 0 : backend.DrawProps(_visible, layer, focus, dissolveFloor);
        int hidden = !layer.CastsShadows || _hidden.Count == 0 ? 0
            : backend.DrawShadowOnlyProps(_hidden, layer, focus, dissolveFloor);
        return new(visible, hidden);
    }
}
