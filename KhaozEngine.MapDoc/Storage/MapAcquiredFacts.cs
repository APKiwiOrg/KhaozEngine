using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Detached facts built before publication. Consumer access never returns owned mutable lists.</summary>
internal sealed class MapAcquiredFacts
{
    readonly SortedDictionary<MapPatchKey, MapPatchRead> _reads = new();
    readonly SortedDictionary<string, MapSurfaceRef> _surfaces = new(StringComparer.Ordinal);
    readonly Dictionary<string, MapRecordRef> _recordIds = new(StringComparer.Ordinal);
    readonly HashSet<MapCoveredRange> _empty = new();
    readonly HashSet<MapUnavailable> _unavailable = new();

    internal IEnumerable<MapPatchRead> Reads => _reads.Values;
    internal IEnumerable<MapSurfaceRef> Surfaces => _surfaces.Values;
    internal IEnumerable<KeyValuePair<MapPatchKey, string>> Present => _reads.Values.Where(r => r.Status == MapPatchStatus.Present)
        .Select(r => new KeyValuePair<MapPatchKey, string>(r.Key, r.SemanticSha256!));
    internal IEnumerable<MapCoveredRange> KnownEmpty => _empty.OrderBy(r => r.SurfaceId, StringComparer.Ordinal)
        .ThenBy(r => r.Slots.MinZ).ThenBy(r => r.Slots.MinX).ThenBy(r => r.Slots.MaxZExclusive).ThenBy(r => r.Slots.MaxXExclusive);
    internal IEnumerable<MapUnavailable> Unavailable => _unavailable.OrderBy(r => r.Key).ThenBy(r => r.What, StringComparer.Ordinal).ThenBy(r => r.Status);
    internal IEnumerable<MapRecordRef> Records => _recordIds.Values.OrderBy(r => r.Anchor).ThenBy(r => r.Id, StringComparer.Ordinal);
    internal bool Complete => _unavailable.Count == 0;
    internal bool HasRead(MapPatchKey key) => _reads.ContainsKey(key);
    internal bool TrySurface(string id, out MapSurfaceRef? surface) => _surfaces.TryGetValue(id, out surface);

    internal void AddSurface(MapSurfaceRef surface)
    {
        if (surface is null || string.IsNullOrWhiteSpace(surface.Id) || surface.Frame is null ||
            !Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy))
            throw new MapDocumentException("invalid acquired surface metadata");
        _ = surface.Frame.WorldXz(MapLatticeAddress.Corner(0, 0));
        _surfaces.Add(surface.Id, CopySurface(surface));
    }
    internal static MapSurfaceRef CopySurface(MapSurfaceRef surface) => surface with
    {
        IndoorSpan = surface.IndoorSpan is { } span ? span with { DomainTags = Array.AsReadOnly(span.DomainTags.ToArray()) } : null,
    };
    internal void AddRead(MapPatchRead read)
    {
        ValidateKey(read.Key);
        if (!Enum.IsDefined(read.Status) || read.PagesRead < 0 || _reads.ContainsKey(read.Key))
            throw new MapDocumentException("incoherent acquired patch result");
        if (read.Status == MapPatchStatus.Present)
        {
            if (read.Patch is null || read.Patch.Key != read.Key || MapSurfaceSemantics.PatchDigest(read.Patch) != read.SemanticSha256)
                throw new MapDocumentException("incoherent acquired patch key or semantic digest");
            read = read with { Patch = read.Patch.Clone() };
            foreach (MapTopologyRecord record in read.Patch.Records)
                if (!_recordIds.TryAdd(record.Id, new(record.Id, read.Key)))
                    throw new MapDocumentException("duplicate acquired record id");
        }
        else if (read.Patch is not null) throw new MapDocumentException("unavailable patch carries a payload");
        _reads.Add(read.Key, read);
        if (read.Status == MapPatchStatus.KnownEmpty) AddEmpty(new(read.Key.SurfaceId, MapSurfaceAcquisitionRead.KeyRange(read.Key)));
        else if (read.Status != MapPatchStatus.Present) AddUnavailable("patch", read.Key, read.Status);
    }
    internal void AddEmpty(MapCoveredRange range)
    {
        if (range is null || string.IsNullOrWhiteSpace(range.SurfaceId)) throw new MapDocumentException("invalid acquired empty range");
        MapSurfacePages.ValidateRect(range.Slots);
        _empty.Add(range);
    }
    internal void AddUnavailable(string what, MapPatchKey key, MapPatchStatus status) => _unavailable.Add(new(what, key, status));
    internal static void ValidateKey(MapPatchKey key)
    {
        if (string.IsNullOrWhiteSpace(key.SurfaceId)) throw new MapDocumentException("invalid acquired patch key");
    }
    internal MapPatchStatus Status(MapPatchKey key) => _reads.TryGetValue(key, out MapPatchRead? read) ? read.Status : MapPatchStatus.Unloaded;
    internal MapSurfacePatch? OwnedPatch(MapPatchKey key) => _reads.TryGetValue(key, out MapPatchRead? read) ? read.Patch : null;
    internal bool HasRecord(MapRecordRef reference) => OwnedPatch(reference.Anchor)?.Records.Any(r => r.Id == reference.Id) == true;
    internal IEnumerable<MapTopologyRecord> OwnedRecords => _reads.Values.Where(r => r.Patch is not null).SelectMany(r => r.Patch!.Records);

    internal MapPatchRead CopyPatch(MapPatchKey key) => _reads.TryGetValue(key, out MapPatchRead? read) &&
        read.Status is MapPatchStatus.Present or MapPatchStatus.KnownEmpty
        ? read with { Patch = read.Patch?.Clone(), PagesRead = 0 }
        : new(key, MapPatchStatus.Unloaded, null, null, "not acquired", 0);
    internal bool CopyRecord(MapRecordRef reference, out MapTopologyRecord? record, out MapPatchStatus status)
    {
        if (!TryRecord(reference, out record, out status)) return false;
        record = OwnedPatch(reference.Anchor)?.Clone().Records.FirstOrDefault(r => r.Id == reference.Id);
        return true;
    }
    internal bool TryRecord(MapRecordRef reference, out MapTopologyRecord? record, out MapPatchStatus status)
    {
        ArgumentNullException.ThrowIfNull(reference);
        status = Status(reference.Anchor);
        record = OwnedPatch(reference.Anchor)?.Records.FirstOrDefault(r => r.Id == reference.Id);
        if (record is not null) return true;
        if (status == MapPatchStatus.Present) status = MapPatchStatus.Missing;
        return false;
    }
    internal IEnumerable<MapTopologyRecord> CopyRecords(MapPatchKey key) => OwnedPatch(key) is { } patch
        ? Array.AsReadOnly(patch.Clone().Records.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray())
        : Array.Empty<MapTopologyRecord>();
}
