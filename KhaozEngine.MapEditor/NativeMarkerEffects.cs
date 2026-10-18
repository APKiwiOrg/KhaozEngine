using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapEditor;

/// <summary>Applies the shared marker diff of one transaction's before and after expansions to its effects.</summary>
internal static class NativeMarkerEffects
{
    /// <summary>The diff's marker IDs and rename rows replace any the preparation named, so unchanged markers are never
    /// reported. Spatial marker changes add their point boxes and Residency only. The one exception is a repair whose
    /// before expansion refuses: no diff is possible, so the transaction keeps the preparation's marker list.</summary>
    internal static MapNativeEditEffects Combine(MapNativeEditEffects effects, MapEffectiveResources before, MapEffectiveResources after)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var diff = MapMarkerEffects.Describe(MapEffectiveAnnotations.Markers(before), MapEffectiveAnnotations.Markers(after));
        return effects with
        {
            OldBounds = NativePlacementEffects.Union(effects.OldBounds, diff.OldBounds),
            NewBounds = NativePlacementEffects.Union(effects.NewBounds, diff.NewBounds),
            OldSpatialBounds = NativePlacementEffects.Union(effects.OldSpatialBounds, diff.OldBounds),
            NewSpatialBounds = NativePlacementEffects.Union(effects.NewSpatialBounds, diff.NewBounds),
            MarkerIds = diff.MarkerIds,
            MarkerNameChanges = diff.MarkerNameChanges,
            Invalidates = effects.Invalidates | diff.Invalidates,
        };
    }
}

/// <summary>The complete expansion of each live native document as last accepted, keyed by the document instance and
/// the closure it was expanded against. Every publication through <see cref="NativeDocumentTransaction.Publish"/>
/// replaces or clears the entry, and a reload or open is a new instance, so a match is the live document's state.</summary>
internal static class NativeAcceptedExpansions
{
    sealed record Entry(MapAssetClosure Closure, Lazy<MapEffectiveResources> Expansion);
    static readonly ConditionalWeakTable<MapDocument, Entry> s_entries = new();

    internal static void Remember(MapDocument live, MapAssetClosure closure, Lazy<MapEffectiveResources> expansion)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(expansion);
        s_entries.AddOrUpdate(live, new(closure, expansion));
    }

    internal static void Forget(MapDocument live) => s_entries.Remove(live);

    internal static bool TryBefore(MapDocument live, MapAssetClosure closure, [MaybeNullWhen(false)] out Lazy<MapEffectiveResources> expansion)
    {
        if (s_entries.TryGetValue(live, out Entry? entry) && ReferenceEquals(entry.Closure, closure))
        {
            expansion = entry.Expansion;
            return true;
        }
        expansion = null;
        return false;
    }
}
