using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>One changed generated foliage batch, keyed by its layer and chunk. A side exists when the layer exists there,
/// the batch is not provably off its raster and it reads at least one stored density chunk. A missing side has null
/// bounds and a null digest. Bounds are the cosmetic world XZ rectangle where the batch may place samples: the chunk's
/// world rectangle clipped to the raster's sample domain, rounded outward to single precision. A digest is the batch
/// dependency digest a generated batch carries. A side whose preparation refuses deterministically carries a digest
/// that binds its status and the read chunks' direct digests instead.</summary>
public sealed record MapFoliageChunkChange(string LayerId, MapFoliageChunkKey Chunk, MapFoliageRect? OldBounds,
    MapFoliageRect? NewBounds, string? BeforeDigest, string? AfterDigest);

/// <summary>One foliage layer whose every batch an edit invalidates: a whole-layer member changed, a layer holding stored
/// chunks was added or removed, or a localized selection exceeded its per-edit ceiling. A missing side has a null digest.
/// A digest binds the layer's settings, every member except density, and its ordered stored-chunk digests, read without
/// any preparation.</summary>
public sealed record MapFoliageLayerChange(string LayerId, string? BeforeDigest, string? AfterDigest);

/// <summary>Foliage effects of one document edit: batch rows and whole-layer entries (T9-P4a). A layer without stored
/// chunks on either side reports nothing, because an all-zero raster places nothing, though the edit still writes the
/// foliage root.</summary>
public static class MapFoliageEffects
{
    /// <summary>The changed batch rows between two complete documents, read from contexts captured from exactly those
    /// documents and their closures. A layer that publishes a whole-layer entry has no rows here, use the overload that
    /// returns the entries too. A context may be null only for a document without foliage. <paramref name="trigger"/> is
    /// the base effect set whose foliage channel these rows replace. It never narrows the diff.</summary>
    public static IReadOnlyList<MapFoliageChunkChange> Describe(MapDocument before, MapDocument after,
        MapFoliageGenerationContext? oldContext, MapFoliageGenerationContext? newContext, MapNativeEditEffects trigger) =>
        Describe(before, after, oldContext, newContext, trigger, out _);

    /// <summary>The changed batch rows and whole-layer entries between two complete documents. Direct rows come from
    /// changed density chunks. Dependent rows come from every batch whose preparation acquisition meets the old or new
    /// bounds of a changed input: support, paint, rooms, masks, consulted compatibility rows, owner correspondences,
    /// markers and clearance owners measured by their proxies. A whole-layer member change, the addition or removal of a
    /// layer holding stored chunks, or a selection past its per-edit ceiling publishes one layer entry instead of rows.
    /// Each context must have been captured from its document and the closure that document names.</summary>
    public static IReadOnlyList<MapFoliageChunkChange> Describe(MapDocument before, MapDocument after,
        MapFoliageGenerationContext? oldContext, MapFoliageGenerationContext? newContext, MapNativeEditEffects trigger,
        out IReadOnlyList<MapFoliageLayerChange> layers)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(trigger);
        Require(before, oldContext, nameof(oldContext));
        Require(after, newContext, nameof(newContext));
        var (rows, entries) = MapFoliageDependents.Describe(before, after,
            () => oldContext ?? throw new ArgumentNullException(nameof(oldContext)),
            () => newContext ?? throw new ArgumentNullException(nameof(newContext)));
        layers = entries;
        return rows;
    }

    // A context must be captured from exactly this document, foliage and everything else, and from the closure the
    // document names. A context from another snapshot refuses even when its foliage matches.
    static void Require(MapDocument document, MapFoliageGenerationContext? context, string name)
    {
        if (context is null)
        {
            if (document.Foliage is not null) throw new ArgumentNullException(name, "a document with foliage needs its context.");
            return;
        }
        if (!string.Equals(context.SourceFingerprint, MapDocumentEditFinalizer.Fingerprint(document), StringComparison.Ordinal) ||
            !string.Equals(context.ClosureHash, context.Assets.Hash, StringComparison.Ordinal) ||
            !context.Assets.Roots.OrderBy(r => r.Id, StringComparer.Ordinal).SequenceEqual(document.NativeAssets.OrderBy(r => r.Id, StringComparer.Ordinal)))
            throw new ArgumentException("the foliage context was not captured from this document and its closure.", name);
    }

    // Every authored layer member except the chunk list, in the closed codec's canonical form.
    internal static string Settings(MapFoliageLayer layer) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-layer-settings/1");
        MapFoliageJson.WriteLayer(w, layer, (_, _) => { });
        w.WriteEndArray();
    });

    internal static string Digest(string settings, MapFoliageChunkKey key, string sha256) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-chunk/1");
        w.WriteStringValue(settings);
        w.WriteNumberValue(key.X);
        w.WriteNumberValue(key.Z);
        w.WriteStringValue(sha256);
        w.WriteEndArray();
    });

    internal static float Down(double value)
    {
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }

    internal static float Up(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }
}
