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
    internal bool IsKnownEmpty(MapPatchKey key) => _empty.Any(r => r.SurfaceId == key.SurfaceId && r.Slots.Contains(key.SlotX, key.SlotZ));
    internal bool HasRead(MapPatchKey key) => _reads.ContainsKey(key);
    internal bool TrySurface(string id, out MapSurfaceRef? surface) => _surfaces.TryGetValue(id, out surface);

    internal void AddSurface(MapSurfaceRef surface)
    {
        if (surface is null || string.IsNullOrWhiteSpace(surface.Id) || surface.Frame is null ||
            !Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy))
            throw new MapDocumentException(surface is null || string.IsNullOrWhiteSpace(surface.Id) || surface.Frame is null
                ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaEnumUnsupported, "invalid acquired surface metadata")
            {
                SurfaceId = surface?.Id,
                FieldPath = surface is null ? "surface" : string.IsNullOrWhiteSpace(surface.Id) ? nameof(surface.Id)
                    : surface.Frame is null ? nameof(surface.Frame) : !Enum.IsDefined(surface.Role) ? nameof(surface.Role) : nameof(surface.PresencePolicy),
                EnumType = surface is not null && !string.IsNullOrWhiteSpace(surface.Id) && surface.Frame is not null
                    ? !Enum.IsDefined(surface.Role) ? typeof(MapSurfaceRole) : typeof(MapPresencePolicy) : null,
                ActualInteger = surface is not null && !string.IsNullOrWhiteSpace(surface.Id) && surface.Frame is not null
                    ? !Enum.IsDefined(surface.Role) ? (long)surface.Role : (long)surface.PresencePolicy : null,
            };
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
            throw new MapDocumentException(!Enum.IsDefined(read.Status) ? MapDocumentRefusal.SchemaEnumUnsupported
                : read.PagesRead < 0 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.IdentityDuplicate,
                "incoherent acquired patch result")
            {
                Patch = read.Key, PatchStatus = read.Status,
                FieldPath = !Enum.IsDefined(read.Status) ? nameof(read.Status) : read.PagesRead < 0 ? nameof(read.PagesRead) : nameof(read.Key),
                EnumType = !Enum.IsDefined(read.Status) ? typeof(MapPatchStatus) : null,
                ActualInteger = !Enum.IsDefined(read.Status) ? (long)read.Status : read.PagesRead < 0 ? read.PagesRead : null,
                MinimumInteger = Enum.IsDefined(read.Status) && read.PagesRead < 0 ? 0 : null,
            };
        if (read.Status == MapPatchStatus.Present)
        {
            if (read.Patch is null || read.Patch.Key != read.Key || MapSurfaceSemantics.PatchDigest(read.Patch) != read.SemanticSha256)
                throw new MapDocumentException(read.Patch is null ? MapDocumentRefusal.SchemaValueRequired
                    : read.Patch.Key != read.Key ? MapDocumentRefusal.AuthoredValueMismatch : MapDocumentRefusal.DigestMismatch,
                    "incoherent acquired patch key or semantic digest")
                {
                    Patch = read.Patch is not null && read.Patch.Key != read.Key ? read.Patch.Key : read.Key, PatchStatus = read.Status,
                    ExpectedPatch = read.Patch is not null && read.Patch.Key != read.Key ? read.Key : null,
                    FieldPath = read.Patch is null ? nameof(read.Patch) : read.Patch.Key != read.Key ? "Patch.Key" : nameof(read.SemanticSha256),
                    ExpectedHash = read.Patch is not null && read.Patch.Key == read.Key ? read.SemanticSha256 : null,
                    ActualHash = read.Patch is not null && read.Patch.Key == read.Key ? MapSurfaceSemantics.PatchDigest(read.Patch) : null,
                };
            read = read with { Patch = read.Patch.Clone() };
            foreach (MapTopologyRecord record in read.Patch.Records)
                if (!_recordIds.TryAdd(record.Id, new(record.Id, read.Key)))
                    throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, "duplicate acquired record id")
                    { RecordId = record.Id, Patch = read.Key, FieldPath = nameof(record.Id) };
        }
        else if (read.Patch is not null) throw new MapDocumentException(MapDocumentRefusal.AcquisitionContractMismatch, "unavailable patch carries a payload")
            { Patch = read.Key, PatchStatus = read.Status, FieldPath = nameof(read.Patch) };
        _reads.Add(read.Key, read);
        if (read.Status == MapPatchStatus.KnownEmpty) AddEmpty(new(read.Key.SurfaceId, MapSurfaceAcquisitionRead.KeyRange(read.Key)));
        else if (read.Status != MapPatchStatus.Present) AddUnavailable("patch", read.Key, read.Status);
    }
    internal void AddEmpty(MapCoveredRange range)
    {
        if (range is null || string.IsNullOrWhiteSpace(range.SurfaceId))
            throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "invalid acquired empty range")
            { SurfaceId = range?.SurfaceId, FieldPath = range is null ? "range" : nameof(range.SurfaceId) };
        MapSurfacePages.ValidateRect(range.Slots);
        _empty.Add(range);
    }
    internal void AddUnavailable(string what, MapPatchKey key, MapPatchStatus status) => _unavailable.Add(new(what, key, status));
    internal static void ValidateKey(MapPatchKey key)
    {
        if (string.IsNullOrWhiteSpace(key.SurfaceId))
            throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "invalid acquired patch key")
            { Patch = key, SurfaceId = key.SurfaceId, FieldPath = nameof(key.SurfaceId) };
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
