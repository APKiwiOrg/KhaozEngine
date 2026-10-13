using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Carries the catalog's steep companions into a <see cref="TileGroundMaterialSet"/>: each material's
/// <see cref="GroundMaterialSteep"/> becomes its layer's <see cref="TileGroundSteepLayer"/>, the companion id
/// turned into the slot that id occupies and the band resolved to its defaults. Also the set's own check, so a
/// hand-built set obeys the same rule a catalog-built one does.</summary>
static class TileGroundSteepCompanions
{
    /// <summary>Sets the companion of every layer whose material carries one. <paramref name="ids"/> is the set's
    /// slot order, ascending. The catalog is checked first, because a companion that does not resolve would
    /// otherwise land in the reserved slot and paint a cliff magenta.</summary>
    internal static void Apply(TileWorldCatalogs catalogs, List<ushort> ids, TileGroundLayerImage[] layers)
    {
        GroundMaterialSteepRules.ThrowIfInvalid(catalogs, "ground material set");
        for (int slot = 0; slot < ids.Count; slot++)
        {
            if (catalogs.Materials[ids[slot]].Steep is not { } steep) continue;
            layers[slot].Steep = new TileGroundSteepLayer
            {
                CompanionSlot = ids.BinarySearch(steep.Material),
                FromDegrees = steep.ResolvedFromDegrees,
                ToDegrees = steep.ResolvedToDegrees,
            };
        }
    }

    /// <summary>Refuses a set whose reserved missing-material layer carries a companion, or whose companion
    /// names that layer. The missing slot is the magenta that marks a dangling id, and a companion either side of
    /// it would hide or spread that signal. The rest is <see cref="TileGroundSteepShading.Validate"/>, run here too
    /// so the failure surfaces where the set is built rather than at the upload.</summary>
    internal static void CheckSet(IReadOnlyList<TileGroundLayerImage> layers, int missingSlot)
    {
        TileGroundSteepShading.Validate(layers);
        if (layers[missingSlot].Steep is not null)
            throw new ArgumentException(
                $"layer {missingSlot} is the reserved missing-material layer and cannot carry a steep companion.",
                nameof(layers));
        for (int slot = 0; slot < missingSlot; slot++)
            if (layers[slot].Steep is { } steep && (steep.CompanionSlot < 0 || steep.CompanionSlot >= missingSlot))
                throw new ArgumentException(
                    $"layer {slot} names slot {steep.CompanionSlot} as its steep companion, which is not one of the " +
                    $"set's material slots 0 to {missingSlot - 1}.", nameof(layers));
    }
}
