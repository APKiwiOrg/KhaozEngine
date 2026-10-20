using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public sealed record MapPreparedInputResult(MapRenderAssetStatus Status, MapPreparedInputLease? Input, string? Detail);

/// <summary>A retained selected mesh/material input. Only the final lease releases its decoded charge.</summary>
public sealed class MapPreparedInputLease : IDisposable
{
    sealed class Shared(MapRenderAssets assets, MapRenderMaterials.MaterialSet materials, string proofDigest)
    {
        internal readonly object Gate = new();
        internal int Owners = 1;
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
    public long RetainedBytes { get { RequireLive(); return _shared.Assets.RetainedBytes + _shared.Materials.RetainedBytes; } }

    public static MapPreparedInputResult LoadSelected(MapAssetClosure closure, IReadOnlyList<string> assetIds,
        IReadOnlyList<string> materialIds, MapAssetExtentProofSet proofs, GltfDecodeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        var loaded = MapRenderAssets.LoadSelected(closure, assetIds, proofs, budget);
        if (loaded.Assets is not { } assets) return new(loaded.Status, null, loaded.Detail);
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
        catch (MapRenderAssetRefusal ex) { return new(ex.Status, null, ex.Message); }
        finally { if (!success) { materials?.Dispose(); assets.Dispose(); } }
    }

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
            _shared.Materials.Dispose(); _shared.Assets.Dispose();
        }
    }
}
