using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapEditor;

// Preparation edits a detached candidate. Accept alone advances the command's retry and undo state.
internal interface INativeDocumentCommand
{
    NativeDocumentPreparation Prepare(MapDocument candidate, bool undo);
}

internal interface INativeBoundDocumentCommand
{
    NativeDocumentPreparation Prepare(MapDocument candidate, bool undo, MapAssetClosure assets);
}
internal enum MapNativePublicationMode { NamedWriteSet, CompleteSnapshot }
internal sealed record NativeDocumentPreparation(MapNativeWriteSet WriteSet, MapNativeEditEffects Effects, Action Accept)
{
    internal MapNativePublicationMode Mode { get; init; } = MapNativePublicationMode.NamedWriteSet;
}
internal sealed record NativeStagedPublication(MapDocument Candidate, MapNativeWriteSet WriteSet,
    MapNativePublicationMode Mode, MapAssetClosure? Assets, MapNativeEditEffects Effects, Action Accept)
{
    /// <summary>The candidate's complete expansion against <see cref="Assets"/>, when staging built one.</summary>
    internal Lazy<MapEffectiveResources>? Expansion { get; init; }
}

/// <summary>Validates and publishes exactly one native document write set.</summary>
internal static class NativeDocumentTransaction
{
    /// <summary>Runs <paramref name="command"/> as one native transaction and returns its effects. A placement
    /// command's effects come from <see cref="NativePlacementEffects.Describe"/> over the document before and the
    /// validated publication after. They carry old and new bounds from <paramref name="placementBounds"/> when both it
    /// and <paramref name="assets"/> are given. Otherwise, as on the local-only direct Apply and Revert paths, which
    /// have no closure, a placement edit that changes geometry reports
    /// <see cref="MapNativeInvalidation.Unbounded"/> rather than claiming bounds it cannot compute.</summary>
    internal static MapNativeEditEffects Run(MapDocument document, IEditorCommand command, bool undo,
        MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false,
        INativePlacementBounds? placementBounds = null)
    {
        var staged = Stage(document, command, undo, assets, registry, localOnly, placementBounds);
        Publish(document, staged);
        staged.Accept();
        return staged.Effects;
    }

    internal static void Publish(MapDocument document, NativeStagedPublication staged)
    {
        if (staged.Mode == MapNativePublicationMode.CompleteSnapshot) NativeDocumentSnapshot.Publish(document, staged.Candidate);
        else NativeDocumentSnapshot.PublishWriteSet(document, staged.Candidate, staged.WriteSet);
        // Only an expansion already built during staging is a snapshot of what was published. Anything else clears it.
        if (staged is { Assets: { } assets, Expansion: { IsValueCreated: true } expansion }) NativeAcceptedExpansions.Remember(document, assets, expansion);
        else NativeAcceptedExpansions.Forget(document);
    }

    internal static NativeStagedPublication Stage(MapDocument document, IEditorCommand command, bool undo,
        MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false,
        INativePlacementBounds? placementBounds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException("Native editing requires a complete document, not a windowed document.");
        MapAssetClosure? selected = command is INativeAssetClosureCommand closures ? closures.Closure(undo) : assets;
        if (command is INativeBoundDocumentCommand && selected is null)
            throw new MapDocumentException("Prefab editing requires a verified asset closure.");
        if (!localOnly && selected is null)
            throw new MapDocumentException("Native editing requires an explicit asset closure binding.");
        registry ??= MapDocRegistry.CreateDefault();
        MapDocument candidate = NativeDocumentSnapshot.Clone(document, registry);
        NativeDocumentPreparation preparation = command switch
        {
            INativeBoundDocumentCommand bound => bound.Prepare(candidate, undo, selected!),
            TerrainEditCommand terrain => terrain.Prepare(candidate, undo, selected, registry),
            INativeDocumentCommand native => native.Prepare(candidate, undo),
            INativePlacementCommand placement => PreparePlacement(document, candidate, placement, undo, selected),
            _ => throw new MapDocumentException("This command does not support atomic native document editing."),
        };
        MapDocument publication;
        if (preparation.Mode == MapNativePublicationMode.CompleteSnapshot) publication = NativeDocumentSnapshot.Clone(candidate, registry);
        else
        {
            publication = NativeDocumentSnapshot.Clone(document, registry);
            NativeDocumentSnapshot.PublishWriteSet(publication, candidate, preparation.WriteSet);
        }
        if (localOnly) MapBoundDocumentValidation.ValidateLocal(publication, registry);
        else MapBoundDocumentValidation.Validate(publication, selected!, registry);
        var beforeAssets = command is INativeAssetClosureCommand pair ? pair.Closure(!undo) : assets;
        // Validation and effects read one complete expansion of each document, built on first use.
        var expanded = selected is null ? null : Expansion(publication, selected);
        // The accepted expansion of the live document is its before side, when it was expanded against the same closure.
        var expandedBefore = (beforeAssets ?? selected) is { } closure
            ? NativeAcceptedExpansions.TryBefore(document, closure, out var accepted) ? accepted : Expansion(document, closure) : null;
        ValidateSurfaces(publication, preparation.WriteSet, preparation.Mode, selected, expanded);
        if (expanded is not null && (publication.Prefabs is not null || publication.NativeResourceTransforms is not null || publication.BuildingIntent is not null))
        {
            var effective = expanded.Value;
            RequireValid(MapPrismCoverage.Validate(effective.CompleteView(), new()));
            MapBoundDocumentValidation.ValidateEffectiveWater(effective, selected!);
        }
        var effects = NativePlacementEffects.Describe(document, publication, selected, registry, placementBounds, beforeAssets, expandedBefore, expanded);
        var named = NativePlacementEffects.NameResources(preparation.Effects, document, publication, preparation.WriteSet);
        effects = NativePlacementEffects.Combine(named, effects,
            document, publication, beforeAssets, selected, preparation.WriteSet, preparation.Mode, expandedBefore, expanded);
        if (expandedBefore is not null && expanded is not null && (HoldsMarkers(document) || HoldsMarkers(publication)))
            effects = MarkerEffects(effects, expandedBefore, expanded);
        return new(publication, preparation.WriteSet, preparation.Mode, selected, effects, preparation.Accept) { Expansion = expanded };
    }

    static bool HoldsMarkers(MapDocument document) => document.Prefabs is not null || document.BuildingIntent is { Markers.Count: > 0 };

    // One diff of the complete before and after expansions, whatever the write set. A marker whose support the edit
    // removed refuses with the after expansion, before publication. An old document that no longer resolves keeps the
    // released effects, as a repair cannot know its old markers.
    static MapNativeEditEffects MarkerEffects(MapNativeEditEffects effects, Lazy<MapEffectiveResources> before, Lazy<MapEffectiveResources> after)
    {
        MapEffectiveResources old;
        try { old = before.Value; }
        catch (MapDocumentException) { return effects; }
        return NativeMarkerEffects.Combine(effects, old, after.Value);
    }

    static NativeDocumentPreparation PreparePlacement(MapDocument before, MapDocument candidate,
        INativePlacementCommand placement, bool undo, MapAssetClosure? assets)
    {
        Action accept = placement.Prepare(candidate, undo, assets);
        var patches = before.Surfaces.Patches.Keys.Union(candidate.Surfaces.Patches.Keys).Where(k =>
            !before.Surfaces.Patches.TryGetValue(k, out var a) || !candidate.Surfaces.Patches.TryGetValue(k, out var b) ||
            MapSurfaceSemantics.PatchDigest(a) != MapSurfaceSemantics.PatchDigest(b)).ToArray();
        var transformed = (before.NativeResourceTransforms ?? []).Concat(candidate.NativeResourceTransforms ?? [])
            .Where(p => before.NativeResourceTransforms?.FirstOrDefault(t => t.Kind == p.Kind && t.OwnerId == p.OwnerId)?.Transform !=
                candidate.NativeResourceTransforms?.FirstOrDefault(t => t.Kind == p.Kind && t.OwnerId == p.OwnerId)?.Transform).ToArray();
        patches = patches.Union(before.Surfaces.Patches.Values.Concat(candidate.Surfaces.Patches.Values).Where(p =>
            transformed.Any(t => t.Kind == MapPrefabChildKind.Surface && t.OwnerId == p.Key.SurfaceId ||
                t.Kind == MapPrefabChildKind.Record && p.Records.Any(r => r.Id == t.OwnerId))).Select(p => p.Key)).ToArray();
        var surfaces = before.Surfaces.Refs.Union(candidate.Surfaces.Refs).Where(s =>
            before.Surfaces.Refs.Find(r => r.Id == s.Id) != candidate.Surfaces.Refs.Find(r => r.Id == s.Id)).Select(s => s.Id).Distinct().ToArray();
        surfaces = surfaces.Union(transformed.Where(t => t.Kind == MapPrefabChildKind.Surface).Select(t => t.OwnerId)).ToArray();
        var writes = new MapNativeWriteSet(patches, surfaces, [], [], [], [], true)
        {
            Prefabs = before.Prefabs?.Digest() != candidate.Prefabs?.Digest(),
            BuildingIntent = before.BuildingIntent?.Digest() != candidate.BuildingIntent?.Digest(),
            NativeResourceTransforms = !(before.NativeResourceTransforms ?? []).SequenceEqual(candidate.NativeResourceTransforms ?? []),
            NumericReservations = true,
        };
        if (placement is INativePlacementWriteSetCommand { AcceptedWrites: { } acceptedWrites })
            writes = NativePlacementResourceState.Union(writes, acceptedWrites);
        return new(writes, new(null, null, [], [], [], [], MapNativeInvalidation.Placements), accept);
    }

    /// <summary>One complete expansion of a document that does not change while it is read. A refusal is kept and
    /// thrown again, as a second expansion of the same inputs would refuse.</summary>
    static Lazy<MapEffectiveResources> Expansion(MapDocument document, MapAssetClosure assets) =>
        new(() => MapPrefabResolver.Expand(document, assets), LazyThreadSafetyMode.None);

    static void ValidateSurfaces(MapDocument publication, MapNativeWriteSet writeSet, MapNativePublicationMode mode, MapAssetClosure? assets,
        Lazy<MapEffectiveResources>? expanded)
    {
        MapSurfaceSet surfaces = publication.Surfaces;
        foreach (MapPatchKey key in mode == MapNativePublicationMode.CompleteSnapshot ? (IEnumerable<MapPatchKey>)surfaces.Patches.Keys : writeSet.Patches)
            if (surfaces.Patches.TryGetValue(key, out MapSurfacePatch? patch)) RequireValid(patch.ValidateLocal());
        MapScopedSurfaces view = expanded is not null && (publication.Prefabs is not null || publication.NativeResourceTransforms is not null)
            ? expanded.Value.CompleteView() : MapScopedSurfaces.CompleteView(surfaces);
        foreach (MapSurfaceSeam seam in surfaces.AllRecords().OfType<MapSurfaceSeam>())
            RequireValid(MapSeamValidator.Validate(seam, view));
        // An edited owner can invalidate a dependency in an otherwise untouched patch.
        foreach (MapSurfacePatch patch in surfaces.Patches.Values.Where(p => p.CornerDependencies.Count != 0))
            RequireValid(MapSeamValidator.ValidateCornerDependencies(patch, view));
        // Payloads contain records, and surface geometry can also change a space's bound coverage.
        if (mode == MapNativePublicationMode.CompleteSnapshot || writeSet.Prefabs || writeSet.BuildingIntent || writeSet.NativeResourceTransforms ||
            writeSet.Patches.Count != 0 || writeSet.SurfaceIds.Count != 0 ||
            writeSet.RecordIds.Count != 0 || writeSet.SpaceIds.Count != 0)
            RequireValid(MapSpaceCoverageValidator.Validate(view));
        // A loaded document carries no containment certificate, so even placement-only publications recheck water.
        if (publication.Water is { } water && (assets is null || publication.Prefabs is null && publication.NativeResourceTransforms is null))
        {
            MapWaterContainmentReport containment = MapWaterContainment.Validate(view, water);
            if (containment.Errors.Count != 0) throw new MapDocumentException(containment.Errors[0]);
            if (!containment.Complete) throw new MapDocumentException("water containment is unverified");
        }
    }

    static void RequireValid(IReadOnlyList<string> findings)
    {
        if (findings.Count != 0) throw new MapDocumentException(string.Join("\n", findings));
    }

}
