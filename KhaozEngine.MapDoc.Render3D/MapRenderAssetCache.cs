using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;
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
        IReadOnlyList<string> materialIds, MapAssetExtentProofSet proofs, GltfDecodeBudget budget, CancellationToken cancellation, MapRenderAdmission.CpuLease? encoded = null, MapRenderAssetLimits? assetLimits = null)
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
            var allowance = new MapRenderAssetAllowance(assetLimits);
            string key = allowance.Key(MapPlacementDigest.Hash(w =>
            {
                w.WriteStartArray(); w.WriteStringValue(closure.Hash); w.WriteStringValue(proofs.Digest);
                w.WriteStartArray(); foreach (string id in selected) w.WriteStringValue(id); w.WriteEndArray();
                w.WriteStartArray(); foreach (string id in required) w.WriteStringValue(id); w.WriteEndArray(); w.WriteEndArray();
            }));
            if (_inputs.TryGetValue(key, out var cached))
            {
                try { return new(MapRenderAssetStatus.Complete, Bind(cached), null); }
                catch (MapSceneCapacityException error)
                { return new(MapRenderAssetStatus.CapacityExceeded, null, error.Message) { Capacity = error.Capacity }; }
            }
            // CPU headroom is reserved before decode. The scene ledger observes actual decoder ownership and workspace.
            MapSceneCapacity? capacity = null;
            var cpu = _admission?.TryReserveCpu(budget.RemainingBytes, out capacity);
            if (capacity is not null) return new(MapRenderAssetStatus.CapacityExceeded, null, capacity.Message) { Capacity = capacity };
            var added = new List<string>();
            try
            {
                var assetKeys = selected.ToDictionary(id => id, id => allowance.Key(Fingerprint(closure, id, proofs)), StringComparer.Ordinal);
                var materialKeys = required.ToDictionary(id => id, id => Fingerprint(closure, id, null), StringComparer.Ordinal);
                using var borrowed = MapPreparedInputBudget.Acquire(assetKeys.Values.Concat(materialKeys.Values)
                    .Where(_pieces.ContainsKey).Select(k => _pieces[k]));
                using var decoderWorkspace = new MapRenderDecodeWorkspace(budget);
                var pieces = new List<MapPreparedInputLease>();
                foreach (string id in selected)
                {
                    string pieceKey = assetKeys[id];
                    if (!_pieces.TryGetValue(pieceKey, out var piece))
                    {
                        long before = budget.OwnedBytes;
                        cancellation.ThrowIfCancellationRequested();
                        var loaded = MapRenderAssets.LoadSelected(closure, [id], proofs, budget, allowance.Remaining);
                        if (loaded.Capacity is { } refused) throw allowance.Refuse(refused);
                        if (loaded.Assets is not { } assets) throw new MapRenderAssetRefusal(loaded.Status, loaded.Detail ?? "Mesh decode refused.");
                        try { piece = MapPreparedInputLease.AssetOnly(assets, proofs.Digest, budget); }
                        catch { assets.Dispose(); throw; }
                        Track(piece, budget.OwnedBytes - before, cpu, encoded); _pieces.Add(pieceKey, piece); added.Add(pieceKey);
                    }
                    allowance.Record(piece.Assets);
                    pieces.Add(piece);
                }
                foreach (string id in required)
                {
                    string pieceKey = materialKeys[id];
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
                Track(input, budget.OwnedBytes - owned, cpu, encoded);
                decoderWorkspace.Finish();
                MapPreparedInputLease selectedInput;
                try { selectedInput = Bind(input, decoderWorkspace.Workspace); }
                catch { input.Dispose(); throw; }
                _inputs.Add(key, input);
                return new(MapRenderAssetStatus.Complete, selectedInput, null);
            }
            catch (OperationCanceledException)
            {
                foreach (string pieceKey in added) { _pieces[pieceKey].Dispose(); _pieces.Remove(pieceKey); }
                throw;
            }
            catch (Exception error) when (error is MapSceneCapacityException or MapRenderAssetRefusal or MapDocumentException or GltfDecodeRefusal or OverflowException)
            {
                foreach (string pieceKey in added) { _pieces[pieceKey].Dispose(); _pieces.Remove(pieceKey); }
                return new(MapRenderAssets.Status(error), null, error.Message) { Capacity = MapRenderDecodeCapacity.From(error) };
            }
            finally { cpu?.Dispose(); }
        }
    }
    static MapPreparedInputLease Bind(MapPreparedInputLease input, MapPreparationReservation? workspace = null)
    {
        var selected = input.Retain();
        try { selected.BindBudget(MapPreparedInputBudget.Acquire([input], workspace)); return selected; }
        catch { selected.Dispose(); throw; }
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
            // Remove preserves Dictionary enumeration on our .NET target. No frame scratch is needed.
            foreach (var pair in entries)
                if (pair.Value.OwnerCount == 1) { pair.Value.Dispose(); entries.Remove(pair.Key); }
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
