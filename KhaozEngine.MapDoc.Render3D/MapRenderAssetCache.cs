using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Content-addressed immutable mesh/material pieces. Plans and upload readers retain them independently.</summary>
public sealed class MapRenderAssetCache : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<string, MapPreparedInputLease> _pieces = new(StringComparer.Ordinal);
    readonly Dictionary<string, MapPreparedInputLease> _inputs = new(StringComparer.Ordinal);
    readonly MapRenderAdmission? _admission;
    long _retained;
    bool _disposed;
    public long RetainedBytes => Interlocked.Read(ref _retained);
    public MapRenderAssetCache() { }
    internal MapRenderAssetCache(MapRenderAdmission admission) => _admission = admission;
    public MapPreparedInputResult Acquire(MapAssetClosure closure, IReadOnlyList<string> assetIds,
        IReadOnlyList<string> materialIds, MapAssetExtentProofSet proofs, GltfDecodeBudget budget) =>
        Acquire(closure, assetIds, materialIds, proofs, budget, CancellationToken.None);
    internal MapPreparedInputResult Acquire(MapAssetClosure closure, IReadOnlyList<string> assetIds,
        IReadOnlyList<string> materialIds, MapAssetExtentProofSet proofs, GltfDecodeBudget budget, CancellationToken cancellation, MapRenderAdmission.CpuLease? encoded = null)
    {
        ArgumentNullException.ThrowIfNull(closure); ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentNullException.ThrowIfNull(materialIds); ArgumentNullException.ThrowIfNull(proofs); ArgumentNullException.ThrowIfNull(budget);
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (proofs.ClosureHash != closure.Hash) return new(MapRenderAssetStatus.Stale, null, "Cache proof belongs to another closure.");
            var selected = assetIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var required = materialIds.Concat(selected.SelectMany(id => closure.GetAsset(id).MaterialResourceIds)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (string id in selected)
                if (!proofs.ByAsset.ContainsKey(id)) return new(MapRenderAssetStatus.Stale, null, "Selected asset lacks an extent proof.");
            string key = MapPlacementDigest.Hash(w =>
            {
                w.WriteStartArray(); w.WriteStringValue(closure.Hash); w.WriteStringValue(proofs.Digest);
                w.WriteStartArray(); foreach (string id in selected) w.WriteStringValue(id); w.WriteEndArray();
                w.WriteStartArray(); foreach (string id in required) w.WriteStringValue(id); w.WriteEndArray(); w.WriteEndArray();
            });
            if (_inputs.TryGetValue(key, out var cached)) return new(MapRenderAssetStatus.Complete, cached.Retain(), null);
            // The full remaining decoder peak is admitted before any strict decode, including cached-set misses.
            var cpu = _admission?.TryReserveCpu(budget.RemainingBytes);
            if (_admission is not null && cpu is null) return new(MapRenderAssetStatus.CapacityExceeded, null, "Decoder peak exceeds aggregate CPU allowance.");
            var added = new List<string>();
            try
            {
                var pieces = new List<MapPreparedInputLease>();
                foreach (string id in selected)
                {
                    string pieceKey = Fingerprint(closure, id, proofs);
                    if (!_pieces.TryGetValue(pieceKey, out var piece))
                    {
                        long before = budget.OwnedBytes;
                        cancellation.ThrowIfCancellationRequested();
                        var loaded = MapRenderAssets.LoadSelected(closure, [id], proofs, budget);
                        if (loaded.Assets is not { } assets) throw new MapRenderAssetRefusal(loaded.Status, loaded.Detail ?? "Mesh decode refused.");
                        try { piece = MapPreparedInputLease.AssetOnly(assets, proofs.Digest, budget); }
                        catch { assets.Dispose(); throw; }
                        Track(piece, budget.OwnedBytes - before, cpu, encoded); _pieces.Add(pieceKey, piece); added.Add(pieceKey);
                    }
                    pieces.Add(piece);
                }
                foreach (string id in required)
                {
                    string pieceKey = Fingerprint(closure, id, null);
                    if (!_pieces.TryGetValue(pieceKey, out var piece))
                    {
                        long before = budget.OwnedBytes;
                        cancellation.ThrowIfCancellationRequested();
                        var materials = (MapRenderMaterials.MaterialSet)MapRenderMaterials.Load(closure, [id], budget);
                        piece = MapPreparedInputLease.MaterialsOnly(closure, materials, proofs.Digest);
                        Track(piece, budget.OwnedBytes - before, cpu, encoded); _pieces.Add(pieceKey, piece); added.Add(pieceKey);
                    }
                    pieces.Add(piece);
                }
                cancellation.ThrowIfCancellationRequested();
                long owned = budget.OwnedBytes;
                var input = MapPreparedInputLease.Compose(closure, proofs.Digest, pieces, budget);
                Track(input, budget.OwnedBytes - owned, cpu, encoded); _inputs.Add(key, input);
                return new(MapRenderAssetStatus.Complete, input.Retain(), null);
            }
            catch (OperationCanceledException)
            {
                foreach (string pieceKey in added) { _pieces[pieceKey].Dispose(); _pieces.Remove(pieceKey); }
                throw;
            }
            catch (Exception error) when (error is MapRenderAssetRefusal or MapDocumentException or GltfDecodeRefusal or OverflowException)
            {
                foreach (string pieceKey in added) { _pieces[pieceKey].Dispose(); _pieces.Remove(pieceKey); }
                return new(MapRenderAssets.Status(error), null, error.Message);
            }
            finally { cpu?.Dispose(); }
        }
    }
    void Track(MapPreparedInputLease input, long owned, MapRenderAdmission.CpuLease? cpu, MapRenderAdmission.CpuLease? encoded)
    {
        var retained = cpu?.Split(owned); var source = encoded?.Retain(); Interlocked.Add(ref _retained, owned);
        input.OnFinalRelease(() => { retained?.Dispose(); source?.Dispose(); Interlocked.Add(ref _retained, -owned); });
    }
    static string Fingerprint(MapAssetClosure closure, string id, MapAssetExtentProofSet? proofs) => MapPlacementDigest.Hash(w =>
    {
        w.WriteStartArray(); w.WriteStringValue(proofs is null ? "material" : "asset"); w.WriteStringValue(id);
        IEnumerable<string> ids = [id];
        if (proofs is not null)
        {
            var asset = closure.GetAsset(id);
            var options = MapJsonTextOptions.CreateSerializer(false); options.IncludeFields = true;
            JsonSerializer.Serialize(w, asset, options); JsonSerializer.Serialize(w, proofs.ByAsset[id], options);
            ids = new[] { asset.MeshResourceId }.Concat(asset.LodResourceIds);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string resource in ids.Order(StringComparer.Ordinal)) Resource(resource);
        w.WriteEndArray();
        void Resource(string resource)
        {
            if (!seen.Add(resource)) return;
            var row = closure.GetResource(resource); w.WriteStringValue(resource); w.WriteStringValue(row.Reference.Sha256);
            w.WriteNumberValue((int)row.Kind); w.WriteNumberValue(row.Reference.PayloadVersion);
            foreach (string child in row.Dependencies.Order(StringComparer.Ordinal)) Resource(child);
        }
    });
    public void Trim() { lock (_gate) TrimCore(); }
    internal void TryTrim()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try { TrimCore(); }
        finally { Monitor.Exit(_gate); }
    }
    void TrimCore()
    {
        Evict(_inputs); Evict(_pieces);
        static void Evict(Dictionary<string, MapPreparedInputLease> entries)
        {
            foreach (string key in entries.Where(p => p.Value.OwnerCount == 1).Select(p => p.Key).ToArray())
            { entries[key].Dispose(); entries.Remove(key); }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var input in _inputs.Values) input.Dispose(); _inputs.Clear();
            foreach (var input in _pieces.Values) input.Dispose(); _pieces.Clear();
        }
    }
}
