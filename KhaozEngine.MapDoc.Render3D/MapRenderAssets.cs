using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

public enum MapRenderAssetStatus { Complete, Missing, Stale, Unsupported, Invalid, CapacityExceeded, NotRepresentable }
/// <param name="MaxDecodedBytes">Demand sites: MapRenderAssetAllowance, MapRenderAssets. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxParts">Demand sites: MapRenderAssetAllowance, MapRenderAssets. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxLods">Demand sites: MapRenderAssets. See docs/CAPACITY-DEMAND.md.</param>
public sealed record MapRenderAssetLimits(long MaxDecodedBytes = 268435456, int MaxParts = 4096, int MaxLods = 16);
public sealed record MapRenderAssetResult(MapRenderAssetStatus Status, MapRenderAssets? Assets, string? Detail)
{
    public MapSceneCapacity? Capacity { get; init; }
}
public sealed class MapRenderAssetRefusal : Exception
{
    public MapRenderAssetStatus Status { get; }
    public MapSceneCapacity? Capacity => InnerException is { } error ? MapRenderDecodeCapacity.From(error) : null;
    public MapRenderAssetRefusal(MapRenderAssetStatus status, string detail) : base(detail) => Status = status;
    public MapRenderAssetRefusal(MapRenderAssetStatus status, string detail, Exception innerException) : base(detail, innerException) => Status = status;
}

public sealed class MapRenderLod
{
    readonly GltfMeshPart[] _parts;
    public int Index { get; }
    public string ResourceId { get; }
    public IReadOnlyList<GltfMeshPart> Parts => Array.AsReadOnly(_parts.Select(MapRenderCopies.Part).ToArray());
    internal IReadOnlyList<GltfMeshPart> BorrowParts => _parts;
    public MapRenderLod(int index, string resourceId, IReadOnlyList<GltfMeshPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        Index = index; ResourceId = resourceId; _parts = parts.Select(MapRenderCopies.Part).ToArray();
    }
}

public sealed class MapRenderAsset
{
    public string AssetId { get; }
    public IReadOnlyList<MapRenderLod> Lods { get; }
    public MapResolvedAsset Descriptor { get; }
    public MapRenderAsset(string assetId, IReadOnlyList<MapRenderLod> lods, MapResolvedAsset descriptor)
    {
        ArgumentNullException.ThrowIfNull(lods); ArgumentNullException.ThrowIfNull(descriptor);
        AssetId = assetId; Lods = Array.AsReadOnly(lods.ToArray()); Descriptor = descriptor;
    }
}

/// <summary>Owns only selected decoded mesh tiers. The verified encoded closure has an independent lifetime.</summary>
public sealed class MapRenderAssets : IDisposable
{
    readonly GltfDecodeBudget _budget;
    bool _disposed;
    public IReadOnlyDictionary<string, MapRenderAsset> ById { get; }
    public long RetainedBytes { get; }
    public MapAssetClosure Closure { get; }
    MapRenderAssets(MapAssetClosure closure, Dictionary<string, MapRenderAsset> assets, GltfDecodeBudget budget, long retainedBytes)
    {
        Closure = closure; ById = new ReadOnlyDictionary<string, MapRenderAsset>(assets);
        _budget = budget; RetainedBytes = retainedBytes;
    }

    internal static MapRenderAssets Borrow(MapAssetClosure closure, IEnumerable<MapRenderAsset> assets) =>
        new(closure, assets.ToDictionary(a => a.AssetId, StringComparer.Ordinal), new(new(MaxPeakBytes: 0)), 0);

    public static MapRenderAssetResult LoadSelected(MapAssetClosure closure, IReadOnlyList<string> assetIds,
        MapAssetExtentProofSet proofs, GltfDecodeBudget budget, MapRenderAssetLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(closure); ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentNullException.ThrowIfNull(proofs); ArgumentNullException.ThrowIfNull(budget);
        limits ??= new();
        long owned = 0;
        try
        {
            if (limits.MaxDecodedBytes < 0 || limits.MaxParts < 0 || limits.MaxLods < 0)
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Asset limits must be non-negative.");
            if (proofs.ClosureHash != closure.Hash) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Extent proof closure differs from the selected input.");
            var selected = new Dictionary<string, MapRenderAsset>(StringComparer.Ordinal);
            int partCount = 0;
            foreach (string id in assetIds.Distinct(StringComparer.Ordinal))
            {
                MapResolvedAsset asset;
                try { asset = closure.GetAsset(id); }
                catch (MapDocumentException ex) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, ex.Message); }
                if (!proofs.ByAsset.TryGetValue(id, out var proof) || proof.ValidationVersion != 1 || proof.SourceUnitsToMetres != asset.SourceUnitsToMetres)
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Selected asset has no current extent proof.");
                foreach (string materialId in asset.MaterialResourceIds) MapRenderMaterials.ReadDefinition(closure, materialId, budget);
                string[] resources = new[] { asset.MeshResourceId }.Concat(asset.LodResourceIds).ToArray();
                if (DemandDiagnostics.Check(budget, "MapRenderAssets.MaxLods.1", "MaxLods", 0, resources.Length, limits.MaxLods, resources.Length > limits.MaxLods, CapacityDemandLifetime.Inventory)) throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxLods), resources.Length, limits.MaxLods);
                var lods = new List<MapRenderLod>();
                for (int lod = 0; lod < resources.Length; lod++)
                {
                    string resourceId = resources[lod];
                    var resource = closure.GetResource(resourceId);
                    if (!proof.Resources.Any(p => p.ResourceId == resourceId && p.Sha256 == resource.Reference.Sha256))
                        throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Extent proof resource differs from the selected tier.");
                    var demand = GltfDecodeAdmission.Measure(closure.BorrowVerifiedResourceBytes(resourceId), budget);
                    if (!demand.Allowed) throw demand.Capacity is { } capacity
                        ? MapRenderDecodeCapacity.Refuse(MapRenderDecodeCapacity.Convert(capacity)) : Capacity();
                    if (DemandDiagnostics.Check(budget, "MapRenderAssets.MaxDecodedBytes.1", "MaxDecodedBytes", owned, demand.OwnedBytes, limits.MaxDecodedBytes, demand.OwnedBytes > limits.MaxDecodedBytes - owned, CapacityDemandLifetime.Inventory)) throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxDecodedBytes), (decimal)owned + demand.OwnedBytes, limits.MaxDecodedBytes);
                    if (DemandDiagnostics.Check(budget, "MapRenderAssets.MaxParts.1", "MaxParts", partCount, demand.PartCount, limits.MaxParts, demand.PartCount > limits.MaxParts - partCount, CapacityDemandLifetime.Inventory)) throw MapRenderDecodeCapacity.Refuse(nameof(MapRenderAssetLimits.MaxParts), (long)partCount + demand.PartCount, limits.MaxParts);
                    var parts = NativeMapAssetLoader.LoadParts(asset, closure, budget, lod == 0 ? null : resourceId);
                    owned = checked(owned + demand.OwnedBytes); partCount = checked(partCount + parts.Count);
                    MapRenderDecodeCapacity.Reserve(budget, 0, demand.OwnedBytes);
                    try { lods.Add(new(lod, resourceId, parts)); }
                    finally { budget.ReleaseTemporary(demand.OwnedBytes); }
                }
                selected.Add(id, new(id, lods, asset));
            }
            var result = new MapRenderAssets(closure, selected, budget, owned);
            owned = 0;
            return new(MapRenderAssetStatus.Complete, result, null);
        }
        catch (Exception ex) when (ex is MapRenderAssetRefusal or MapDocumentException or GltfDecodeRefusal or InvalidDataException or InvalidOperationException or OverflowException)
        { return new(Status(ex), null, ex.Message) { Capacity = MapRenderDecodeCapacity.From(ex) }; }
        finally { if (owned != 0) budget.ReleaseOwned(owned); }
    }

    internal static MapRenderAssetStatus Status(Exception ex) => ex switch
    {
        MapSceneCapacityException => MapRenderAssetStatus.CapacityExceeded,
        MapRenderAssetRefusal refusal => refusal.Status,
        GltfDecodeRefusal { Status: GltfDecodeStatus.CapacityExceeded } => MapRenderAssetStatus.CapacityExceeded,
        GltfDecodeRefusal { Status: GltfDecodeStatus.Unsupported } => MapRenderAssetStatus.Unsupported,
        OverflowException => MapRenderAssetStatus.CapacityExceeded,
        _ => MapRenderAssetStatus.Invalid
    };
    static MapRenderAssetRefusal Capacity() => new(MapRenderAssetStatus.CapacityExceeded, "Selected decoded assets exceed their admitted limits.");
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _budget.ReleaseOwned(RetainedBytes);
    }
}
