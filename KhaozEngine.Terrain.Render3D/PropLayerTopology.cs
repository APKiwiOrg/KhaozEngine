using System;
using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>The layer-list rules <see cref="Scene3DChunkSink"/> enforces when it takes a layer list, at
    /// construction and again in <see cref="Scene3DChunkSink.UpdateLayers"/>, plus the stricter shape test a partial
    /// refresh needs (<see cref="Scene3DChunkSink.KeepsLayerShape"/>). Pure, so each rule is headless-testable.
    /// <para>Three levels, each stricter than the last. <see cref="Validate"/> is what any list must satisfy to be
    /// built from at all. <see cref="RequireSameTopology"/> is what a replacement list must keep so the sink's
    /// per-layer state stays valid: the placement buckets and the HLOD gate are derived from the placement layers
    /// and the per-layer HLOD presence, and every loaded chunk's placement arrays, cluster keys and HLOD handles
    /// are indexed by layer. <see cref="OnlyGenerationDiffers"/> is what a replacement must keep when only SOME
    /// loaded chunks are refreshed afterwards: a chunk that is not refreshed keeps placements derived from the old
    /// companion hosts and clusters holding the old draw settings, so nothing but the generation configs may
    /// change.</para></summary>
    internal static class PropLayerTopology
    {
        /// <summary>The construction rules: at least one layer, every companion hosted by an in-range scatter or
        /// placement layer, and every other layer carrying a scatter config or placements.</summary>
        internal static void Validate(PropLayer[] layers, string paramName)
        {
            if (layers.Length == 0)
                throw new ArgumentException("At least one PropLayer is required.", paramName);
            for (int i = 0; i < layers.Length; i++)
            {
                PropLayer l = layers[i];
                if (l.IsCompanion)
                {
                    if (l.HostLayerIndex < 0 || l.HostLayerIndex >= layers.Length)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion HostLayerIndex {l.HostLayerIndex} is out of range.", paramName);
                    if (layers[l.HostLayerIndex].IsCompanion)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion host {l.HostLayerIndex} must be a scatter or placement layer.",
                            paramName);
                }
                else if (l.Scatter == null && !l.IsPlacement)
                {
                    throw new ArgumentException(
                        $"PropLayer {i} has no Scatter config, Companions config, Placements, or PlacementSource.",
                        paramName);
                }
            }
        }

        /// <summary>The replacement rules: the same count and, per index, the same kind and HLOD presence, with every
        /// placement layer unchanged. A companion may name a different host, which is safe only when every loaded
        /// chunk is rebuilt afterwards.</summary>
        internal static void RequireSameTopology(PropLayer[] before, PropLayer[] after, string paramName)
        {
            if (after.Length != before.Length)
                throw new ArgumentException("Layer count is topology. Rebuild the sink to add or remove a layer.", paramName);
            for (int i = 0; i < after.Length; i++)
            {
                PropLayer was = before[i], now = after[i];
                if (was.IsCompanion != now.IsCompanion || was.IsPlacement != now.IsPlacement || was.HasHlod != now.HasHlod)
                    throw new ArgumentException($"Layer {i} changed topology. Rebuild the sink instead.", paramName);
                if (was.IsPlacement && !was.Equals(now))
                    throw new ArgumentException($"Placement layer {i} is construction-only.", paramName);
            }
        }

        /// <summary>True when <paramref name="after"/> has the same count as <paramref name="before"/> and each layer
        /// differs from its counterpart at most in <see cref="PropLayer.Scatter"/> and
        /// <see cref="PropLayer.Companions"/>. Kind, companion host, HLOD, placements, meshes, radii, fades, shadow
        /// policy and identity all compare equal.</summary>
        internal static bool OnlyGenerationDiffers(PropLayer[] before, IReadOnlyList<PropLayer> after)
        {
            if (after.Count != before.Length) return false;
            for (int i = 0; i < before.Length; i++)
            {
                PropLayer now = after[i];
                if (!now.Equals(before[i].WithGeneration(now.Scatter, now.Companions))) return false;
            }
            return true;
        }
    }
}
