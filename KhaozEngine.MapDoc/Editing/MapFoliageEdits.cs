using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

public abstract record MapFoliageEdit(string Label);

/// <summary>Adds a whole layer or replaces the layer with the same ID in place. A supported change to an existing layer's
/// policy definition needs the digest <see cref="MapFoliageEdits.PreviewPolicyChange"/> issued for the same before
/// document and proposal.</summary>
public sealed record MapSetFoliageLayer(MapFoliageLayer Layer, string? ExpectedDifferentialDigest = null) : MapFoliageEdit("Set foliage layer");

/// <summary>Replaces the samples [X, X + Width) by [Z, Z + Depth) of one layer. Density is row-major, rows advance positive
/// world Z, and every value is from 0 to 255.</summary>
public sealed record MapSetFoliageDensity(string LayerId, int X, int Z, int Width, int Depth, IReadOnlyList<int> Density)
    : MapFoliageEdit("Set foliage density");

/// <summary>Paints one circular brush in world metres. See <see cref="MapFoliageBrush"/>.</summary>
public sealed record MapPaintFoliage(string LayerId, float WorldX, float WorldZ, float RadiusMetres, byte Target, float Hardness)
    : MapFoliageEdit("Paint foliage");

/// <summary>Removes one layer. Removing the last layer leaves the document without foliage.</summary>
public sealed record MapRemoveFoliage(string LayerId) : MapFoliageEdit("Remove foliage layer");

/// <summary>Foliage steps in order, with an optional owner edit, prepared as one candidate and validated together.</summary>
public sealed record MapFoliageEditSequence(IReadOnlyList<MapFoliageEdit> Edits, MapPrefabEdit? OwnerEdit) : MapFoliageEdit("Edit foliage");

/// <summary>Detached foliage preparation against the matching verified closure. Every step applies to a detached
/// candidate that shares unchanged immutable layers, and the candidate is validated once before a result exists, so a
/// refusal leaves nothing behind. A raster edit reaches at most 64 chunks and visits at most 65,536 addresses. The
/// candidate is finalized once by <see cref="MapDocumentEditFinalizer"/>, whose batch rows are the effects. A foliage-only
/// edit expands the document at most once, shared by both sides, since expansion never reads foliage, and a no-op edit
/// expands nothing.</summary>
public static class MapFoliageEdits
{
    public static MapPrefabEditResult Prepare(MapDocument document, MapAssetClosure assets, MapFoliageEdit edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(edit);
        MapDocumentEditFinalizer.Preflight(document, assets);
        if (edit is not MapFoliageEditSequence sequence)
        {
            MapFoliageSet? foliage = Apply(document.Foliage, edit);
            MapDocument candidate = MapPrefabEditPreparation.Copy(document);
            candidate.Foliage = foliage;
            MapEffectiveResources? expanded = Validate(document, candidate, assets, [edit], owners: false);
            return Result(document, candidate, assets, null, new(null, expanded, Shared: true));
        }
        if (sequence.Edits is null) throw new MapDocumentException("A foliage edit sequence requires its edit list.");
        MapFoliageSet? set = document.Foliage;
        foreach (MapFoliageEdit step in sequence.Edits)
        {
            if (step is null) throw new MapDocumentException("A foliage edit sequence must not contain null.");
            set = Apply(set, step);
        }
        MapPrefabEditResult? owner = null;
        MapDocument after;
        if (sequence.OwnerEdit is { } ownerEdit)
        {
            // The owner edit prepares its own detached candidate without foliage, then the foliage joins it and the
            // whole candidate is validated once, so no intermediate owner or foliage state is ever accepted alone.
            MapDocument withoutFoliage = MapPrefabEditPreparation.Copy(document);
            withoutFoliage.Foliage = null;
            owner = MapPrefabEdits.PrepareRaw(withoutFoliage, assets, ownerEdit);
            // That expansion read the document without its foliage, which expansion never reads, so it is the
            // document's own snapshot, and its provenance is recorded for the foliage context that reuses it.
            if (owner.Expansions?.Before is { } read) read.SourceFingerprint ??= MapDocumentEditFinalizer.BaseFingerprint(withoutFoliage);
            after = owner.After;
        }
        else after = MapPrefabEditPreparation.Copy(document);
        after.Foliage = set;
        MapEffectiveResources? validated = Validate(document, after, assets, sequence.Edits, owners: owner is not null);
        return Result(document, after, assets, owner, owner is null ? new(null, validated, Shared: true) : new(owner.Expansions?.Before, validated));
    }

    /// <summary>The stable-lattice differential between the document and its proposal with this one layer set. Only a
    /// Complete result issues the digest that <see cref="MapSetFoliageLayer.ExpectedDifferentialDigest"/> binds.</summary>
    public static MapFoliageDifferentialResult PreviewPolicyChange(MapDocument document, MapAssetClosure assets, MapSetFoliageLayer edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(edit);
        MapDocument proposal = Proposal(document, edit);
        MapBoundDocumentValidation.Validate(proposal, assets);
        return MapFoliageDifferential.Compare(document, proposal, assets, edit.Layer.Id);
    }

    static MapDocument Proposal(MapDocument document, MapSetFoliageLayer edit)
    {
        MapFoliageSet? foliage = Apply(document.Foliage, edit);
        MapDocument proposal = MapPrefabEditPreparation.Copy(document);
        proposal.Foliage = foliage;
        return proposal;
    }

    static MapFoliageSet? Apply(MapFoliageSet? set, MapFoliageEdit edit)
    {
        switch (edit)
        {
            case MapSetFoliageLayer replace:
                return SetLayer(set, replace.Layer ?? throw new MapDocumentException("A foliage layer set requires a layer."));
            case MapSetFoliageDensity density:
                return Replace(set, density.LayerId, layer => SetDensity(layer, density));
            case MapPaintFoliage paint:
                return Replace(set, paint.LayerId, layer => MapFoliageBrush.Apply(layer, paint));
            case MapRemoveFoliage remove:
                return Remove(set, remove.LayerId);
            case MapFoliageEditSequence:
                throw new MapDocumentException("A foliage edit sequence cannot contain another sequence.");
            default:
                throw new MapDocumentException("Unknown foliage edit.");
        }
    }

    // The layer is copied in, so the request's layer object never becomes document state. Every advertised chunk must
    // be resident in the request, since no document can read density another storage only advertised.
    static MapFoliageSet SetLayer(MapFoliageSet? set, MapFoliageLayer value)
    {
        var resident = value.Chunks.Select(c => c.Key).ToHashSet();
        if (value.ChunkDigests.FirstOrDefault(d => !resident.Contains(d.Key)) is { } missing)
            throw new MapDocumentException($"Foliage layer '{value.Id}' advertises chunk {missing.Key} without its density bytes.");
        MapFoliageLayer layer = value.Copy();
        var layers = (set?.Layers ?? []).ToList();
        int index = layers.FindIndex(l => string.Equals(l.Id, layer.Id, StringComparison.Ordinal));
        if (index >= 0) layers[index] = layer;
        else layers.Add(layer);
        return new(set?.PayloadVersion ?? 1, layers);
    }

    static MapFoliageSet? Remove(MapFoliageSet? set, string id)
    {
        int index = Require(set, id);
        MapFoliageLayer[] layers = set!.Layers.Where((_, i) => i != index).ToArray();
        return layers.Length == 0 ? null : new(set.PayloadVersion, layers);
    }

    static MapFoliageSet Replace(MapFoliageSet? set, string id, Func<MapFoliageLayer, MapFoliageLayer> edit)
    {
        int index = Require(set, id);
        MapFoliageLayer[] layers = set!.Layers.ToArray();
        layers[index] = edit(layers[index]);
        return new(set.PayloadVersion, layers);
    }

    static int Require(MapFoliageSet? set, string id)
    {
        int index = set is null ? -1 : set.Layers.ToList().FindIndex(l => string.Equals(l.Id, id, StringComparison.Ordinal));
        return index >= 0 ? index : throw new MapDocumentException($"No foliage layer '{id}'.");
    }

    // Shape, domain and budget refuse before any value is read. The values are copied once, so the request list is never
    // read again and never reaches the layer.
    static MapFoliageLayer SetDensity(MapFoliageLayer layer, MapSetFoliageDensity edit)
    {
        MapFoliageBrush.RequireRaster(layer);
        if (edit.Width < 1 || edit.Depth < 1) throw new MapDocumentException("A foliage density rectangle needs a positive width and depth.");
        if (edit.X < 0 || edit.Z < 0 || (long)edit.X + edit.Width > layer.Width || (long)edit.Z + edit.Depth > layer.Depth)
            throw new MapDocumentException($"The foliage density rectangle lies outside layer '{layer.Id}' samples {layer.Width} by {layer.Depth}.");
        int hiX = edit.X + edit.Width - 1, hiZ = edit.Z + edit.Depth - 1;
        MapFoliageBrush.RequireBudget(layer, edit.X, edit.Z, hiX, hiZ);
        if (edit.Density is null) throw new MapDocumentException("A foliage density edit requires its values.");
        int count = edit.Width * edit.Depth;
        if (edit.Density.Count != count)
            throw new MapDocumentException($"A {edit.Width} by {edit.Depth} foliage density rectangle needs {count} values, not {edit.Density.Count}.");
        var values = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int value = edit.Density[i];
            if ((uint)value > byte.MaxValue)
                throw new MapDocumentException($"Foliage density value {value} at row {i / edit.Width}, column {i % edit.Width} must be from 0 to 255.");
            values[i] = (byte)value;
        }
        return MapFoliageBrush.Rewrite(layer, edit.X, edit.Z, hiX, hiZ, (x, z, _) => values[(z - edit.Z) * edit.Width + (x - edit.X)]);
    }

    // The candidate passes the complete bound document gate. A new reference that only effective resources can answer
    // needs one expansion, which validates every foliage owner. A policy definition change needs its differential token.
    static MapEffectiveResources? Validate(MapDocument before, MapDocument after, MapAssetClosure assets, IReadOnlyList<MapFoliageEdit> steps, bool owners)
    {
        MapBoundDocumentValidation.Validate(after, assets);
        MapEffectiveResources? expanded = owners || NewEffectiveReferences(before, after) ? MapPrefabResolver.Expand(after, assets) : null;
        foreach (MapSetFoliageLayer step in steps.OfType<MapSetFoliageLayer>()) RequireDifferential(before, assets, step, after);
        return expanded;
    }

    // The token binds the candidate that lands. A preview token therefore matches only a candidate equal to the before
    // document plus that one layer, and a mixed sequence carrying a policy change refuses as stale.
    static void RequireDifferential(MapDocument before, MapAssetClosure assets, MapSetFoliageLayer step, MapDocument proposal)
    {
        MapFoliageLayer layer = step.Layer;
        MapFoliageLayer? old = before.Foliage?.Layers.FirstOrDefault(l => string.Equals(l.Id, layer.Id, StringComparison.Ordinal));
        if (old is null || SamePolicy(old.Policy, layer.Policy)) return;
        if (string.IsNullOrEmpty(step.ExpectedDifferentialDigest))
            throw new MapDocumentException($"Foliage layer '{layer.Id}': a policy definition change needs the differential digest " +
                "PreviewPolicyChange issues for this document and proposal.");
        MapFoliageDifferentialResult result = MapFoliageDifferential.Compare(before, proposal, assets, layer.Id);
        if (result.Status != MapFoliageStatus.Complete)
            throw new MapDocumentException($"Foliage layer '{layer.Id}': the policy differential is {result.Status}, so it issues no digest.");
        if (!string.Equals(result.Digest, step.ExpectedDifferentialDigest, StringComparison.Ordinal))
            throw new MapDocumentException($"Foliage layer '{layer.Id}': the differential digest is stale for this document and proposal. " +
                "Preview the policy change again.");
    }

    static bool SamePolicy(MapFoliagePolicy a, MapFoliagePolicy b) => string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&
        a.Version == b.Version && BitConverter.SingleToInt32Bits(a.NormalBoundaryHeight) == BitConverter.SingleToInt32Bits(b.NormalBoundaryHeight);

    // Declared support floors are fully checked by the bound gate. Space, bound prefab surface and owner references
    // need effective resources unless the before document already carried them.
    static bool NewEffectiveReferences(MapDocument before, MapDocument after)
    {
        var known = References(before.Foliage).ToHashSet();
        var declared = (after.Surfaces?.Refs ?? []).Where(s => s.Role == MapSurfaceRole.SupportFloor).Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);
        return References(after.Foliage).Any(r => !known.Contains(r) && !(r.Kind == "Surface" && declared.Contains(r.Id)));
    }

    static IEnumerable<(string Kind, string Id)> References(MapFoliageSet? set)
    {
        foreach (MapFoliageLayer layer in set?.Layers ?? [])
        {
            yield return Binding(layer.Support);
            foreach (MapFoliageCompatibilityOrdinal row in layer.CompatibilityOrdinals) yield return ("Surface", row.SurfaceId);
            foreach (MapFoliageMask mask in layer.ExceptionMasks)
            {
                yield return Owner(mask.Owner);
                yield return Binding(mask.Support);
            }
            foreach (MapFoliageOwnerSupport owner in layer.OwnerSupports)
            {
                yield return Owner(owner.Owner);
                yield return Binding(owner.Support);
            }
        }

        static (string, string) Binding(MapSupportBinding binding) => binding.Kind == MapSupportBindingKind.Space
            ? ("Space", binding.SpaceId ?? "") : ("Surface", binding.SurfaceId ?? "");
        static (string, string) Owner(MapPrefabReference owner) => ("Owner" + owner.Scope + owner.Kind, owner.Id);
    }

    // The owner edit's effects and writes are kept. Finalization adds the foliage write and the batch rows.
    static MapPrefabEditResult Result(MapDocument before, MapDocument after, MapAssetClosure assets, MapPrefabEditResult? owner,
        MapEditExpansions expansions)
    {
        MapNativeWriteSet writes = owner?.WriteSet ?? new MapNativeWriteSet([], [], [], [], [], [], false);
        MapNativeEditEffects effects = owner?.Effects ?? new MapNativeEditEffects(null, null, [], [], [], [], MapNativeInvalidation.None);
        return MapDocumentEditFinalizer.Complete(before, new(after, writes, effects) { Expansions = expansions }, assets, assets);
    }
}
