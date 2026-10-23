using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapPreparedInputResult(MapRenderAssetStatus Status, MapPreparedInputLease? Input, string? Detail)
{
    public MapSceneCapacity? Capacity { get; init; }
}

/// <summary>A retained selected mesh/material input. Only the final lease releases its decoded charge.</summary>
public sealed class MapPreparedInputLease : IDisposable
{
    sealed class Shared(MapRenderAssets assets, MapRenderMaterials.MaterialSet materials, string proofDigest)
    {
        internal readonly object Gate = new();
        internal int Owners = 1;
        internal Action? FinalRelease;
        internal long? RetainedOverride;
        internal MapRenderAssets Assets => assets;
        internal MapRenderMaterials.MaterialSet Materials => materials;
        internal string ProofDigest => proofDigest;
    }
    readonly Shared _shared;
    int _disposed;
    MapPreparedInputLease(Shared shared) => _shared = shared;
    void RequireLive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public MapRenderAssets Assets { get { RequireLive(); return _shared.Assets; } }
    public IReadOnlyDictionary<string, MapRenderMaterial> Materials { get { RequireLive(); return _shared.Materials; } }
    public string ClosureHash => Assets.Closure.Hash;
    public string ProofDigest { get { RequireLive(); return _shared.ProofDigest; } }
    public long RetainedBytes { get { RequireLive(); return _shared.RetainedOverride ?? (_shared.Assets.RetainedBytes + _shared.Materials.RetainedBytes); } }

    public static MapPreparedInputResult LoadSelected(MapAssetClosure closure, IReadOnlyList<string> assetIds,
        IReadOnlyList<string> materialIds, MapAssetExtentProofSet proofs, GltfDecodeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        var loaded = MapRenderAssets.LoadSelected(closure, assetIds, proofs, budget);
        if (loaded.Assets is not { } assets) return new(loaded.Status, null, loaded.Detail) { Capacity = loaded.Capacity };
        MapRenderMaterials.MaterialSet? materials = null;
        bool success = false;
        try
        {
            string[] required = materialIds.Concat(assets.ById.Values.SelectMany(a => a.Descriptor.MaterialResourceIds)).Distinct(StringComparer.Ordinal).ToArray();
            materials = (MapRenderMaterials.MaterialSet)MapRenderMaterials.Load(closure, required, budget);
            var result = new MapPreparedInputLease(new(assets, materials, proofs.Digest));
            success = true;
            materials = null;
            return new(MapRenderAssetStatus.Complete, result, null);
        }
        catch (MapRenderAssetRefusal ex) { return new(ex.Status, null, ex.Message) { Capacity = ex.Capacity }; }
        finally { if (!success) { materials?.Dispose(); assets.Dispose(); } }
    }

    internal static MapPreparedInputLease AssetOnly(MapRenderAssets assets, string proof, GltfDecodeBudget budget)
    {
        MapRenderDecodeCapacity.Reserve(budget, 256, 0);
        return new(new(assets, new(new(StringComparer.Ordinal), budget, 256), proof));
    }
    internal static MapPreparedInputLease MaterialsOnly(MapAssetClosure closure, MapRenderMaterials.MaterialSet materials, string proof) =>
        new(new(MapRenderAssets.Borrow(closure, []), materials, proof));
    internal static MapPreparedInputLease Compose(MapAssetClosure closure, string proof, IReadOnlyList<MapPreparedInputLease> pieces, GltfDecodeBudget budget)
    {
        long bytes = checked(512L + 64L * pieces.Sum(p => p.Assets.ById.Count + p.Materials.Count));
        MapRenderDecodeCapacity.Reserve(budget, bytes, bytes);
        var retained = new List<MapPreparedInputLease>();
        try
        {
            foreach (var piece in pieces) retained.Add(piece.Retain());
            var assets = MapRenderAssets.Borrow(closure, pieces.SelectMany(p => p.Assets.ById.Values));
            var materials = new MapRenderMaterials.MaterialSet(pieces.SelectMany(p => p.Materials).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), budget, bytes);
            var lease = new MapPreparedInputLease(new(assets, materials, proof) { RetainedOverride = bytes + pieces.Sum(p => p.RetainedBytes) });
            lease.OnFinalRelease(() => { foreach (var piece in retained) piece.Dispose(); });
            return lease;
        }
        catch { foreach (var piece in retained) piece.Dispose(); budget.ReleaseOwned(bytes); throw; }
        finally { budget.ReleaseTemporary(bytes); }
    }

    internal int OwnerCount { get { lock (_shared.Gate) return _shared.Owners; } }
    internal void OnFinalRelease(Action release)
    { lock (_shared.Gate) { RequireLive(); _shared.FinalRelease += release; } }

    public MapPreparedInputLease Retain()
    {
        lock (_shared.Gate)
        {
            RequireLive(); checked { _shared.Owners++; }
            return new(_shared);
        }
    }
    public void Dispose()
    {
        lock (_shared.Gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (--_shared.Owners != 0) return;
            try { _shared.Materials.Dispose(); _shared.Assets.Dispose(); }
            finally { _shared.FinalRelease?.Invoke(); }
        }
    }
}
