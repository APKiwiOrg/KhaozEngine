using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

// Preparation edits a detached candidate. Accept alone advances the command's retry and undo state.
internal interface INativeDocumentCommand
{
    NativeDocumentPreparation Prepare(MapDocument candidate, bool undo);
}

internal sealed record NativeDocumentPreparation(MapNativeWriteSet WriteSet, MapNativeEditEffects Effects, Action Accept);

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
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException("Native editing requires a complete document, not a windowed document.");
        if (!localOnly && assets is null)
            throw new MapDocumentException("Native editing requires an explicit asset closure binding.");
        INativeDocumentCommand native = command switch
        {
            INativeDocumentCommand typed => typed,
            INativePlacementCommand placement => new PlacementAdapter(placement),
            _ => throw new MapDocumentException("This command does not support atomic native document editing."),
        };
        registry ??= MapDocRegistry.CreateDefault();
        MapDocument candidate = NativeDocumentSnapshot.Clone(document, registry);
        NativeDocumentPreparation preparation = native.Prepare(candidate, undo);
        // Validate the state publication will produce. Unlisted candidate edits cannot satisfy dependencies.
        MapDocument publication = NativeDocumentSnapshot.Clone(document, registry);
        NativeDocumentSnapshot.PublishWriteSet(publication, candidate, preparation.WriteSet);
        if (localOnly) MapBoundDocumentValidation.ValidateLocal(publication, registry);
        else MapBoundDocumentValidation.Validate(publication, assets!, registry);
        ValidateSurfaces(publication.Surfaces, preparation.WriteSet);
        // Placement effects need the document before publication and the validated state after it.
        MapNativeEditEffects effects = native is PlacementAdapter
            ? NativePlacementEffects.Describe(document, publication, assets, registry, placementBounds)
            : preparation.Effects;
        NativeDocumentSnapshot.PublishWriteSet(document, publication, preparation.WriteSet);
        preparation.Accept();
        return effects;
    }

    static void ValidateSurfaces(MapSurfaceSet surfaces, MapNativeWriteSet writeSet)
    {
        foreach (MapPatchKey key in writeSet.Patches)
            if (surfaces.Patches.TryGetValue(key, out MapSurfacePatch? patch)) RequireValid(patch.ValidateLocal());
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(surfaces);
        foreach (MapSurfaceSeam seam in surfaces.AllRecords().OfType<MapSurfaceSeam>())
            RequireValid(MapSeamValidator.Validate(seam, view));
        // An edited owner can invalidate a dependency in an otherwise untouched patch.
        foreach (MapSurfacePatch patch in surfaces.Patches.Values.Where(p => p.CornerDependencies.Count != 0))
            RequireValid(MapSeamValidator.ValidateCornerDependencies(patch, view));
        // Payloads contain records, and surface geometry can also change a space's bound coverage.
        if (writeSet.Patches.Count != 0 || writeSet.SurfaceIds.Count != 0 ||
            writeSet.RecordIds.Count != 0 || writeSet.SpaceIds.Count != 0)
            RequireValid(MapSpaceCoverageValidator.Validate(view));
    }

    static void RequireValid(IReadOnlyList<string> findings)
    {
        if (findings.Count != 0) throw new MapDocumentException(string.Join("\n", findings));
    }

    // Run replaces the placeholder effects with the placement diff once the publication has validated.
    sealed class PlacementAdapter(INativePlacementCommand command) : INativeDocumentCommand
    {
        public NativeDocumentPreparation Prepare(MapDocument candidate, bool undo)
        {
            Action accept = command.Prepare(candidate, undo);
            var effects = new MapNativeEditEffects(null, null, Array.Empty<MapPatchKey>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<MapDigestChange>(), MapNativeInvalidation.Placements);
            return new(MapNativeWriteSet.PlacementsOnly, effects, accept);
        }
    }
}
