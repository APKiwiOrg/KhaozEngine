using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    /// <summary>Applies one foliage edit through the native session history with the command the GUI uses, so the public
    /// finalizer supplies its effects. A dry run applies nothing: it previews one layer set's stable-lattice differential
    /// against the open document and returns that identity-bound result, whose digest a later layer set may carry. Every
    /// other edit refuses a dry run. A refusal throws and leaves the session unchanged.</summary>
    public MapFoliageMutationResult FoliageApply(MapFoliageEdit edit, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (dryRun)
        {
            if (edit is not MapSetFoliageLayer layer)
                throw new MapDocumentException("A foliage dry run previews one layer set, not " + edit.Label.ToLowerInvariant() + ".");
            return session.WithNativeAssets(c =>
            {
                MapFoliageDifferentialResult differential = MapFoliageEdits.PreviewPolicyChange(c.Document, c.Assets, layer);
                return Result(false, differential.Status, c.Document, c.Assets, "", differential);
            });
        }
        long events = session.NativeHistoryState.Events;
        MapNativeEditEffects effects = session.ExecuteNativeCommand(new FoliageEditCommand(edit));
        bool applied = session.NativeHistoryState.Events != events;
        return session.WithNativeAssets(c => Result(applied, MapFoliageStatus.Complete, c.Document, c.Assets, applied ? effects.Describe() : "", null));
    }

    MapFoliageMutationResult Result(bool applied, MapFoliageStatus status, MapDocument document, MapAssetClosure assets, string effects,
        MapFoliageDifferentialResult? differential)
    {
        var (undo, redo) = session.NativeHistoryLabels;
        return new(applied, status, Identity(document, assets), effects, session.IsDirty, undo, redo, differential);
    }

    // The whole authored identity under the session's resolver options, as native open and summary report it.
    static string Identity(MapDocument document, MapAssetClosure assets)
    {
        MapResolveOptions options = NativeDocumentService.SessionOptionsFor(document.ResolverIdentity);
        return options.ResolverVersion == 2
            ? MapAuthoredIdentityV2.Compute(document, assets, options)
            : MapAuthoredIdentity.Compute(document, assets, options);
    }
}
