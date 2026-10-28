using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>The effective resources a preparation already expanded for its before and after documents, so finalization
/// captures their contexts without expanding them again. <paramref name="Shared"/> marks a pair that differs only in
/// foliage, which expansion never reads, so one expansion serves both sides.</summary>
internal sealed record MapEditExpansions(MapEffectiveResources? Before, MapEffectiveResources? After, bool Shared = false);

/// <summary>One side of a finalized edit: the document fingerprint and verified closure it was captured from, its
/// effective resources and, once a row needed it, its foliage context. Never serialized. A side is reused only when a
/// later caller presents the same fingerprint and closure hash, never by document bytes alone. A side may also carry the
/// fingerprint without foliage its creator computed from the same document, which its capture then reuses. A side it
/// expands or captures itself takes its caller's query and expansion profile, and a null member keeps the engine default.
/// The profile only decides where expansion refuses, so a reused side never depends on it.</summary>
internal sealed class MapDocumentFinalizationSide
{
    readonly Func<MapEffectiveResources>? _source;
    readonly string? _baseline;
    readonly MapQueryLimits? _queryLimits;
    readonly MapPrefabExpansionLimits? _expansionLimits;

    internal MapDocumentFinalizationSide(string fingerprint, MapAssetClosure assets, MapEffectiveResources? resources,
        Func<MapEffectiveResources>? source = null, string? baseline = null, MapQueryLimits? queryLimits = null,
        MapPrefabExpansionLimits? expansionLimits = null)
    {
        Fingerprint = fingerprint;
        Assets = assets;
        Resources = resources;
        _source = source;
        _baseline = baseline;
        _queryLimits = queryLimits;
        _expansionLimits = expansionLimits;
    }

    internal string Fingerprint { get; }
    internal MapAssetClosure Assets { get; }
    internal MapEffectiveResources? Resources { get; private set; }
    internal MapFoliageGenerationContext? Context { get; private set; }

    internal bool Matches(string fingerprint, MapAssetClosure? assets) => assets is not null &&
        string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal) && string.Equals(Assets.Hash, assets.Hash, StringComparison.Ordinal);

    /// <summary>The side's resources, expanded at most once, after the context preflight admits the document.</summary>
    internal MapEffectiveResources ResourcesFor(MapDocument document) =>
        Resources ??= _source?.Invoke() ?? MapDocumentEditFinalizer.Expand(document, Assets, _queryLimits ?? new(), _expansionLimits ?? new());

    /// <summary>The side's foliage context, captured at most once from exactly the document this side fingerprints.</summary>
    internal MapFoliageGenerationContext Capture(MapDocument document)
    {
        if (Context is { } context) return context;
        MapFoliageContextResult result = MapFoliageGenerationContext.CaptureEffective(document, Assets, ResourcesFor(document), baseline: _baseline,
            queryLimits: _queryLimits, expansionLimits: _expansionLimits);
        if (result.Status != MapFoliageStatus.Complete || result.Context is null)
            throw MapFoliageEditRefusal.Create(result.Status, "the foliage generation context refused this document");
        return Context = result.Context;
    }
}

/// <summary>The finalization of one edit from its before side to its after side, with the foliage rows it published.
/// Preparations, staged publications and accepted command state carry it, so staging an unchanged result and replaying
/// accepted snapshots reuse the captured sides instead of capturing them again.</summary>
internal sealed class MapDocumentFinalizationStamp
{
    internal MapDocumentFinalizationStamp(MapDocumentFinalizationSide before, MapDocumentFinalizationSide after,
        IReadOnlyList<MapFoliageChunkChange> rows, IReadOnlyList<MapFoliageLayerChange> layers)
    {
        Before = before;
        After = after;
        Rows = rows;
        Layers = layers;
    }

    internal MapDocumentFinalizationSide Before { get; }
    internal MapDocumentFinalizationSide After { get; }
    internal IReadOnlyList<MapFoliageChunkChange> Rows { get; }
    internal IReadOnlyList<MapFoliageLayerChange> Layers { get; }

    /// <summary>The same edit read from its after side back to its before side, as undo publishes it.</summary>
    internal MapDocumentFinalizationStamp Reverse() => new(After, Before, ReverseRows(Rows), ReverseLayers(Layers));

    internal static IReadOnlyList<MapFoliageChunkChange> ReverseRows(IReadOnlyList<MapFoliageChunkChange> rows) =>
        Array.AsReadOnly(rows.Select(r => r with
        {
            OldBounds = r.NewBounds,
            NewBounds = r.OldBounds,
            BeforeDigest = r.AfterDigest,
            AfterDigest = r.BeforeDigest,
        }).ToArray());

    internal static IReadOnlyList<MapFoliageLayerChange> ReverseLayers(IReadOnlyList<MapFoliageLayerChange> layers) =>
        Array.AsReadOnly(layers.Select(l => l with { BeforeDigest = l.AfterDigest, AfterDigest = l.BeforeDigest }).ToArray());
}
