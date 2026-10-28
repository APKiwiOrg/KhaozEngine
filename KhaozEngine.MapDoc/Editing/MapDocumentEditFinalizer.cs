using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>The common boundary that completes a prepared document edit. It validates the complete candidate and
/// replaces the edit's foliage channel with the direct and dependent batch rows read from contexts captured from exactly
/// the before and after documents and their closures. Pure prefab, interior, marker, foliage and terrain preparations and
/// the editor's native transactions all finalize here, so every consumer sees the same rows. Every other effect member,
/// flag and write is kept. Finalizing a finalized result again returns the same rows. A pair of documents without
/// foliage is returned unchanged, with no capture.</summary>
public static class MapDocumentEditFinalizer
{
    /// <summary>Finalizes an edit whose before and after documents bind the same verified closure.</summary>
    public static MapPrefabEditResult Finalize(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure assets) =>
        Finalize(before, prepared, assets, assets);

    /// <summary>Finalizes an edit that switches closures, reading each side against its own verified closure.</summary>
    public static MapPrefabEditResult Finalize(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure beforeAssets,
        MapAssetClosure afterAssets) => FinalizeUnder(before, prepared, beforeAssets, afterAssets, null, null);

    /// <summary>Finalizes as the closure-switching overload does, expanding each side and capturing its foliage context
    /// under the caller's query and prefab expansion profile. The profile only decides where finalization refuses.</summary>
    public static MapPrefabEditResult Finalize(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure beforeAssets,
        MapAssetClosure afterAssets, MapQueryLimits queryLimits, MapPrefabExpansionLimits expansionLimits)
    {
        ArgumentNullException.ThrowIfNull(queryLimits);
        ArgumentNullException.ThrowIfNull(expansionLimits);
        return FinalizeUnder(before, prepared, beforeAssets, afterAssets, queryLimits, expansionLimits);
    }

    static MapPrefabEditResult FinalizeUnder(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure beforeAssets,
        MapAssetClosure afterAssets, MapQueryLimits? queryLimits, MapPrefabExpansionLimits? expansionLimits)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(beforeAssets);
        ArgumentNullException.ThrowIfNull(afterAssets);
        ArgumentNullException.ThrowIfNull(prepared.After);
        Preflight(prepared.After, afterAssets);
        MapBoundDocumentValidation.Validate(prepared.After, afterAssets);
        return Complete(before, prepared, beforeAssets, afterAssets, queryLimits, expansionLimits);
    }

    /// <summary>Finalizes a result whose after document its preparer already validated. A side it expands takes the
    /// given profile, and a null member keeps the engine default.</summary>
    internal static MapPrefabEditResult Complete(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure beforeAssets,
        MapAssetClosure afterAssets, MapQueryLimits? queryLimits = null, MapPrefabExpansionLimits? expansionLimits = null)
    {
        MapDocument after = prepared.After;
        Preflight(after, afterAssets);
        // Without foliage nothing is finalized, and released results carry no retained expansion.
        if (before.Foliage is null && after.Foliage is null) return prepared.Expansions is null ? prepared : prepared with { Expansions = null };
        // Each side's fingerprint without foliage is hashed once here and reused by its capture.
        string ba = BaseFingerprint(before), bb = BaseFingerprint(after);
        string fa = Fingerprint(ba, before.Foliage), fb = Fingerprint(bb, after.Foliage);
        bool written = !string.Equals(MapFoliageProjection.Digest(before.Foliage), MapFoliageProjection.Digest(after.Foliage), StringComparison.Ordinal);
        MapNativeWriteSet writes = written && !prepared.WriteSet.Foliage ? prepared.WriteSet with { Foliage = true } : prepared.WriteSet;
        if (prepared.Stamp is { } done && done.Before.Matches(fa, beforeAssets) && done.After.Matches(fb, afterAssets))
            return new(after, writes, Channel(prepared.Effects, done)) { Stamp = done };
        MapEditExpansions? expansions = prepared.Expansions;
        // One shared expansion serves both sides only when they bind the same closure.
        bool shared = expansions?.Shared == true && string.Equals(beforeAssets.Hash, afterAssets.Hash, StringComparison.Ordinal);
        var sideA = new MapDocumentFinalizationSide(fa, beforeAssets, shared ? expansions!.Before ?? expansions.After : expansions?.Before,
            baseline: ba, queryLimits: queryLimits, expansionLimits: expansionLimits);
        var sideB = new MapDocumentFinalizationSide(fb, afterAssets, shared ? expansions!.After ?? expansions.Before : expansions?.After,
            shared ? () => sideA.ResourcesFor(before) : null, bb, queryLimits, expansionLimits);
        var (rows, layers) = MapFoliageDependents.Describe(before, after, () => sideA.Capture(before), () => sideB.Capture(after), shared);
        var stamp = new MapDocumentFinalizationStamp(sideA, sideB, rows, layers);
        return new(after, writes, Channel(prepared.Effects, stamp)) { Stamp = stamp };
    }

    /// <summary>The editor's central finalization of a staged publication. A side is reused from
    /// <paramref name="stamp"/>, or for the before side from <paramref name="accepted"/>, only when its fingerprint and
    /// closure still match. Otherwise only that side is captured again from the staged expansion. Rows and entries are
    /// reused only when both of the stamp's sides match. Returns the input effects unchanged, with no stamp, when neither
    /// document holds foliage.</summary>
    internal static (MapNativeEditEffects Effects, MapDocumentFinalizationStamp? Stamp) Staged(MapDocument before, MapDocument after,
        MapNativeEditEffects effects, MapAssetClosure beforeAssets, MapAssetClosure afterAssets, MapDocumentFinalizationStamp? stamp,
        MapDocumentFinalizationSide? accepted, string beforeFingerprint, string afterFingerprint,
        Func<MapEffectiveResources> beforeResources, Func<MapEffectiveResources> afterResources)
    {
        Preflight(after, afterAssets);
        if (before.Foliage is null && after.Foliage is null) return (effects, null);
        bool matchA = stamp?.Before.Matches(beforeFingerprint, beforeAssets) == true, matchB = stamp?.After.Matches(afterFingerprint, afterAssets) == true;
        if (matchA && matchB) return (Channel(effects, stamp!), stamp);
        MapDocumentFinalizationSide sideA = matchA ? stamp!.Before
            : accepted?.Matches(beforeFingerprint, beforeAssets) == true ? accepted : new(beforeFingerprint, beforeAssets, null, beforeResources);
        MapDocumentFinalizationSide sideB = matchB ? stamp!.After : new(afterFingerprint, afterAssets, null, afterResources);
        var (rows, layers) = MapFoliageDependents.Describe(before, after, () => sideA.Capture(before), () => sideB.Capture(after));
        var staged = new MapDocumentFinalizationStamp(sideA, sideB, rows, layers);
        return (Channel(effects, staged), staged);
    }

    /// <summary>Replaces the foliage channel: the rows, the layer entries, and the Foliage flag exactly when either
    /// exists. Every other member and flag is kept, so finalizing twice is idempotent.</summary>
    internal static MapNativeEditEffects Channel(MapNativeEditEffects effects, MapDocumentFinalizationStamp stamp) => effects with
    {
        FoliageChunks = stamp.Rows,
        FoliageLayers = stamp.Layers,
        Invalidates = (effects.Invalidates & ~MapNativeInvalidation.Foliage) |
            (stamp.Rows.Count != 0 || stamp.Layers.Count != 0 ? MapNativeInvalidation.Foliage : MapNativeInvalidation.None),
    };

    /// <summary>Refuses a document whose foliage context demand exceeds the default construction budget, from metadata
    /// alone. A document without foliage is not measured, so released documents keep their behaviour.</summary>
    internal static void Preflight(MapDocument document, MapAssetClosure? assets) => Preflight(document, assets, document.Foliage);

    /// <summary>Admits proposed foliage against the target's metadata and closure without a document copy.</summary>
    internal static void Preflight(MapDocument document, MapAssetClosure? assets, MapFoliageSet? foliage)
    {
        if (foliage is null) return;
        if (assets is null) throw new MapDocumentException("Native foliage editing requires a verified asset closure.");
        MapFoliageContextMeasure measure = MapFoliageContextPreflight.Measure(document, assets, new(), foliage);
        if (measure.Status != MapFoliageStatus.Complete)
            throw MapFoliageEditRefusal.Create(measure.Status, "the document exceeds the foliage context budget before any copy");
    }

    /// <summary>One expansion of a document whose context demand the preflight first admits, with its provenance
    /// recorded even when the document holds no foliage, since a foliage-only edit shares it with its other side.</summary>
    internal static MapEffectiveResources Expand(MapDocument document, MapAssetClosure assets) =>
        Expand(document, assets, new MapQueryLimits(), new MapPrefabExpansionLimits());

    /// <summary>The expansion of <see cref="Expand(MapDocument, MapAssetClosure)"/> under the caller's query and
    /// prefab expansion profile.</summary>
    internal static MapEffectiveResources Expand(MapDocument document, MapAssetClosure assets, MapQueryLimits queryLimits,
        MapPrefabExpansionLimits expansionLimits)
    {
        Preflight(document, assets);
        MapEffectiveResources resources = MapPrefabResolver.Expand(document, assets, expansionLimits, null, null, queryLimits);
        resources.SourceFingerprint ??= BaseFingerprint(document);
        return resources;
    }

    /// <summary>The semantic identity of a complete document: its fingerprint without foliage and its logical foliage
    /// digest. Two documents that finalize differently never share a fingerprint.</summary>
    internal static string Fingerprint(MapDocument document, MapDocRegistry? registry = null) =>
        Fingerprint(BaseFingerprint(document, registry), document.Foliage);

    /// <summary>The fingerprint of a document whose <see cref="BaseFingerprint"/> is already known.</summary>
    internal static string Fingerprint(string baseline, MapFoliageSet? set)
    {
        string foliage = MapFoliageProjection.Digest(set);
        return MapCanonical.HashHex(w =>
        {
            w.WriteStartArray();
            w.WriteStringValue("kemap/finalization-fingerprint/1");
            w.WriteStringValue(baseline);
            w.WriteStringValue(foliage);
            w.WriteEndArray();
        });
    }

    /// <summary>The identity of everything in a complete document except foliage, which expansion never reads: its world
    /// identity computed from its live lists rather than any stored tile index, its native globals and every patch
    /// digest. Display name, schema text and each surface's cached semantic digest are excluded, since that digest is
    /// derived from patches the fingerprint already binds and a named publication may leave it stale.</summary>
    internal static string BaseFingerprint(MapDocument document, MapDocRegistry? registry = null)
    {
        registry ??= MapDocRegistry.CreateDefault();
        var options = MapDocumentFile.CreateCompactOptions(registry);
        MapSpatialIndex spatial = MapSpatialIndex.Build(document);
        return MapCanonical.HashHex(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("formatVersion", document.FormatVersion);
            w.WriteString("id", document.Id);
            MapCanonical.WriteGlobals(w, document, options);
            w.WriteStartArray("tiles");
            foreach (MapTileCoord tile in spatial.OccupiedTiles)
            {
                w.WriteStartArray();
                w.WriteNumberValue(tile.X); w.WriteNumberValue(tile.Z);
                w.WriteStringValue(MapDocumentHash.OfTile(spatial, tile, registry));
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WritePropertyName("supportRecipe");
            JsonSerializer.Serialize(w, document.SupportRecipe, options);
            w.WritePropertyName("surfaces");
            JsonSerializer.Serialize(w, document.Surfaces.Refs.Select(r => r with { SemanticSha256 = "" }).ToArray(), options);
            w.WritePropertyName("playableBounds");
            JsonSerializer.Serialize(w, document.PlayableBounds, options);
            w.WritePropertyName("nativeAssets");
            JsonSerializer.Serialize(w, document.NativeAssets, options);
            w.WritePropertyName("numericIdHighWaterMark");
            new MapNumericIdJsonConverter().Write(w, document.NumericIdHighWaterMark, options);
            w.WritePropertyName("resolverIdentity");
            JsonSerializer.Serialize(w, document.ResolverIdentity, options);
            w.WriteStartArray("patches");
            foreach (var pair in document.Surfaces.Patches.OrderBy(p => p.Key))
            {
                w.WriteStartArray();
                MapPrefabJson.Write(w, pair.Key);
                w.WriteStringValue(Surfaces.MapSurfaceSemantics.PatchDigest(pair.Value));
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
