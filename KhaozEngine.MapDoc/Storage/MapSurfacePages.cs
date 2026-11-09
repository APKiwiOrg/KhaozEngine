using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Closed page envelopes with bounded cardinalities checked before DTO materialization.</summary>
internal static class MapSurfacePages
{
    internal const int MaxEntries = 256;
    const int MaxMetadataEntries = MapSurfaceStorageLayout.MaxPageBytes / 24;
    static readonly JsonSerializerOptions Options = CreateOptions();
    internal sealed record DirectoryPage(string SurfaceId, MapSlotRect Covers, IReadOnlyList<MapIndexPageRef> Pages);
    internal sealed record IndexPage(string SurfaceId, MapSlotRect Covers, IReadOnlyList<MapSurfaceIndexEntry> Entries);

    static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
    internal static byte[] EncodeDirectory(string surface, MapSlotRect covers, IReadOnlyList<MapIndexPageRef> pages)
    {
        ValidatePages(covers, pages);
        return Encode(new DirectoryPage(surface, covers, pages));
    }
    internal static byte[] EncodeIndex(string surface, MapSlotRect covers, IReadOnlyList<MapSurfaceIndexEntry> entries)
    {
        ValidateEntries(surface, covers, entries);
        return Encode(new IndexPage(surface, covers, entries));
    }
    static byte[] Encode<T>(T page)
    {
        using var stream = new PageOutput();
        using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, page, Options);
        return stream.ToArray();
    }

    internal static IReadOnlyList<MapIndexPageRef> DecodeDirectory(byte[] bytes, MapDirectoryPageRef expected)
    {
        using JsonDocument json = Parse(bytes);
        Members(json.RootElement, "$", "surfaceId", "covers", "pages");
        RectangleMembers(json.RootElement.GetProperty("covers"), "$.covers");
        JsonElement pages = json.RootElement.GetProperty("pages");
        CheckArray(pages, MaxEntries, "$.pages");
        int index = 0;
        foreach (JsonElement pageRef in pages.EnumerateArray())
        {
            string path = $"$.pages[{index++}]";
            Members(pageRef, path, "covers", "sha256", "entryCount"); RectangleMembers(pageRef.GetProperty("covers"), path + ".covers");
        }
        DirectoryPage page = json.RootElement.Deserialize<DirectoryPage>(Options) ?? throw Invalid(MapDocumentRefusal.SchemaValueRequired, "$");
        if (page.SurfaceId != expected.SurfaceId || page.Covers != expected.Covers)
            throw Invalid(page.SurfaceId != expected.SurfaceId ? MapDocumentRefusal.AuthoredValueMismatch : MapDocumentRefusal.BoundsInvalid,
                page.SurfaceId != expected.SurfaceId ? "$.surfaceId" : "$.covers", surface: page.SurfaceId);
        ValidatePages(page.Covers, page.Pages);
        return Array.AsReadOnly(page.Pages.ToArray());
    }
    internal static IReadOnlyList<MapSurfaceIndexEntry> DecodeIndex(byte[] bytes, string surface, MapIndexPageRef expected)
    {
        using JsonDocument json = Parse(bytes);
        Members(json.RootElement, "$", "surfaceId", "covers", "entries");
        RectangleMembers(json.RootElement.GetProperty("covers"), "$.covers");
        JsonElement entries = json.RootElement.GetProperty("entries");
        CheckArray(entries, MaxEntries, "$.entries");
        int index = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string path = $"$.entries[{index++}]";
            Members(entry, path, "key", "cells", "minHeightUnits", "maxHeightUnits", "payloadSha256", "semanticSha256",
                "dependencies", "recordIds", "incidentRecords", "spaceIds");
            foreach (string name in new[] { "dependencies", "recordIds", "incidentRecords", "spaceIds" })
                CheckArray(entry.GetProperty(name), MaxMetadataEntries, path + "." + name);
            RectangleMembers(entry.GetProperty("cells"), path + ".cells");
            KeyMembers(entry.GetProperty("key"), path + ".key");
            int dependencyIndex = 0;
            foreach (JsonElement key in entry.GetProperty("dependencies").EnumerateArray()) KeyMembers(key, $"{path}.dependencies[{dependencyIndex++}]");
            int incidentIndex = 0;
            foreach (JsonElement incident in entry.GetProperty("incidentRecords").EnumerateArray())
            {
                string incidentPath = $"{path}.incidentRecords[{incidentIndex++}]";
                Members(incident, incidentPath, "id", "anchor"); KeyMembers(incident.GetProperty("anchor"), incidentPath + ".anchor");
            }
        }
        IndexPage page = json.RootElement.Deserialize<IndexPage>(Options) ?? throw Invalid(MapDocumentRefusal.SchemaValueRequired, "$");
        int? entryCount = page.SurfaceId == surface && page.Covers == expected.Covers ? page.Entries.Count : null;
        if (page.SurfaceId != surface || page.Covers != expected.Covers || entryCount != expected.EntryCount)
            throw Invalid(page.SurfaceId != surface ? MapDocumentRefusal.AuthoredValueMismatch
                : page.Covers != expected.Covers ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.SchemaArrayLengthMismatch,
                page.SurfaceId != surface ? "$.surfaceId" : page.Covers != expected.Covers ? "$.covers" : "$.entries",
                actualCount: entryCount, expectedCount: entryCount is not null ? expected.EntryCount : null, surface: page.SurfaceId);
        ValidateEntries(surface, page.Covers, page.Entries);
        return Array.AsReadOnly(page.Entries.Select(e => e with
        {
            Dependencies = Array.AsReadOnly(e.Dependencies.ToArray()),
            RecordIds = Array.AsReadOnly(e.RecordIds.ToArray()),
            IncidentRecords = Array.AsReadOnly(e.IncidentRecords.ToArray()),
            SpaceIds = Array.AsReadOnly(e.SpaceIds.ToArray()),
            Loaded = false,
        }).ToArray());
    }
    static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MapSurfaceStorageLayout.MaxPageBytes)
            throw new MapDocumentException(bytes.Length < 1 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded, "invalid surface storage page")
            {
                FieldPath = "$",
                ActualByteCount = bytes.LongLength,
                MinimumInteger = bytes.Length < 1 ? 1 : null,
                Budget = bytes.Length < 1 ? null : "MaxPageBytes",
                Limit = bytes.Length < 1 ? null : MapSurfaceStorageLayout.MaxPageBytes
            };
        JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { ClosedValues(json.RootElement, "$"); return json; }
        catch { json.Dispose(); throw; }
    }
    static void ClosedValues(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string memberPath = path + "." + property.Name;
                if (!seen.Add(property.Name)) throw Invalid(MapDocumentRefusal.SchemaMemberDuplicate, memberPath);
                ClosedValues(property.Value, memberPath);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray()) ClosedValues(item, $"{path}[{index++}]");
        }
        else if (value.ValueKind == JsonValueKind.Null) throw Invalid(MapDocumentRefusal.SchemaMemberNull, path);
    }
    static void Members(JsonElement root, string path, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length)
            throw Invalid(root.ValueKind != JsonValueKind.Object ? MapDocumentRefusal.SchemaObjectRequired : MapDocumentRefusal.SchemaArrayLengthMismatch,
                path, actualCount: root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Count() : null, expectedCount: names.Length);
        foreach (string name in names) if (!root.TryGetProperty(name, out _)) throw Invalid(MapDocumentRefusal.SchemaMemberMissing, path + "." + name);
    }
    static void RectangleMembers(JsonElement value, string path) => Members(value, path, "minX", "minZ", "maxXExclusive", "maxZExclusive");
    static void KeyMembers(JsonElement value, string path) => Members(value, path, "surfaceId", "slotX", "slotZ");
    static void CheckArray(JsonElement array, int limit, string path)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > limit)
            throw new MapDocumentException(array.ValueKind != JsonValueKind.Array ? MapDocumentRefusal.SchemaArrayRequired : MapDocumentRefusal.WorkCapacityExceeded,
                "invalid surface storage page")
            {
                FieldPath = path,
                ActualCount = array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : null,
                Budget = array.ValueKind != JsonValueKind.Array ? null : limit == MaxEntries ? "MaxEntries" : "MaxMetadataEntries",
                Limit = array.ValueKind == JsonValueKind.Array ? limit : null
            };
    }
    internal static void Validate(MapDirectoryPageRef page)
    {
        if (string.IsNullOrWhiteSpace(page.SurfaceId)) throw Invalid(MapDocumentRefusal.SchemaValueRequired, "$.surfaceId", surface: page.SurfaceId);
        ValidateRect(page.Covers); MapSurfaceStorageLayout.RequireDigest(page.Sha256);
    }
    internal static void ValidateRect(MapSlotRect rect, string path = "$.covers")
    {
        if (rect.MinX >= rect.MaxXExclusive || rect.MinZ >= rect.MaxZExclusive) throw Invalid(MapDocumentRefusal.BoundsInvalid, path);
    }
    static bool Within(MapSlotRect inner, MapSlotRect outer) => inner.MinX >= outer.MinX && inner.MinZ >= outer.MinZ &&
        inner.MaxXExclusive <= outer.MaxXExclusive && inner.MaxZExclusive <= outer.MaxZExclusive;
    static void ValidatePages(MapSlotRect covers, IReadOnlyList<MapIndexPageRef> pages)
    {
        ValidateRect(covers);
        int count = RequireCount(pages, "$.pages");
        for (int i = 0; i < count; i++)
        {
            string path = $"$.pages[{i}]";
            MapIndexPageRef page = pages[i] ?? throw Invalid(MapDocumentRefusal.SchemaValueRequired, path);
            ValidateRect(page.Covers, path + ".covers"); MapSurfaceStorageLayout.RequireDigest(page.Sha256);
            bool within = Within(page.Covers, covers);
            if (!within || page.EntryCount is < 1 or > MaxEntries)
                throw new MapDocumentException(!within ? MapDocumentRefusal.BoundsInvalid
                    : page.EntryCount < 1 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded, "invalid surface storage page")
                {
                    FieldPath = path + (!within ? ".covers" : ".entryCount"),
                    Index = i,
                    ActualInteger = within && page.EntryCount < 1 ? page.EntryCount : null,
                    MinimumInteger = within && page.EntryCount < 1 ? 1 : null,
                    ActualCount = within && page.EntryCount > MaxEntries ? page.EntryCount : null,
                    Budget = within && page.EntryCount > MaxEntries ? "MaxEntries" : null,
                    Limit = within && page.EntryCount > MaxEntries ? MaxEntries : null
                };
            for (int j = 0; j < i; j++) if (pages[j].Covers.Overlaps(page.Covers))
                throw new MapDocumentException(MapDocumentRefusal.StorageDirectoryPagesOverlap, "invalid surface storage page")
                { FieldPath = path + ".covers", Index = i, OtherIndex = j };
        }
    }
    static void ValidateEntries(string surface, MapSlotRect covers, IReadOnlyList<MapSurfaceIndexEntry> entries)
    {
        ValidateRect(covers);
        RequireCount(entries, "$.entries");
        MapPatchKey? previous = null;
        int index = 0;
        foreach (MapSurfaceIndexEntry entry in entries)
        {
            string path = $"$.entries[{index}]";
            if (entry is null || entry.Key.SurfaceId != surface || !covers.Contains(entry.Key.SlotX, entry.Key.SlotZ) ||
                (previous is { } key && key.CompareTo(entry.Key) >= 0))
                throw Invalid(entry is null ? MapDocumentRefusal.SchemaValueRequired
                    : entry.Key.SurfaceId != surface ? MapDocumentRefusal.AuthoredOwnershipMismatch
                    : !covers.Contains(entry.Key.SlotX, entry.Key.SlotZ) ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.OrderInvalid,
                    path + (entry is null ? "" : entry.Key.SurfaceId != surface ? ".key.surfaceId" : ".key"),
                    surface: surface, patch: entry?.Key, index: index);
            previous = entry.Key;
            MapSurfaceStorageLayout.RequireDigest(entry.PayloadSha256); MapSurfaceStorageLayout.RequireDigest(entry.SemanticSha256);
            MapCellRect cells = entry.Cells;
            Int128 x = (Int128)entry.Key.SlotX * 64, z = (Int128)entry.Key.SlotZ * 64;
            if (cells.MinX < x || cells.MinZ < z || cells.MaxXExclusive > x + 64 || cells.MaxZExclusive > z + 64 ||
                cells.MinX >= cells.MaxXExclusive || cells.MinZ >= cells.MaxZExclusive || entry.MinHeightUnits > entry.MaxHeightUnits ||
                entry.Dependencies is null || entry.RecordIds is null || entry.IncidentRecords is null || entry.SpaceIds is null)
            {
                bool cellBounds = cells.MinX < x || cells.MinZ < z || cells.MaxXExclusive > x + 64 || cells.MaxZExclusive > z + 64 ||
                    cells.MinX >= cells.MaxXExclusive || cells.MinZ >= cells.MaxZExclusive;
                string field = cellBounds ? ".cells" : entry.MinHeightUnits > entry.MaxHeightUnits ? ".maxHeightUnits"
                    : entry.Dependencies is null ? ".dependencies" : entry.RecordIds is null ? ".recordIds"
                    : entry.IncidentRecords is null ? ".incidentRecords" : ".spaceIds";
                throw Invalid(cellBounds || entry.MinHeightUnits > entry.MaxHeightUnits ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.SchemaValueRequired,
                    path + field, surface: surface, patch: entry.Key, index: index);
            }
            if (entry.Dependencies.Any(k => string.IsNullOrWhiteSpace(k.SurfaceId))) Required("dependencies");
            if (entry.RecordIds.Any(string.IsNullOrWhiteSpace)) Required("recordIds");
            if (entry.SpaceIds.Any(string.IsNullOrWhiteSpace)) Required("spaceIds");
            if (entry.IncidentRecords.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Anchor.SurfaceId))) Required("incidentRecords");
            Unique(entry.Dependencies, path + ".dependencies", entry.Key, index);
            Unique(entry.RecordIds, path + ".recordIds", entry.Key, index);
            Unique(entry.IncidentRecords, path + ".incidentRecords", entry.Key, index);
            Unique(entry.SpaceIds, path + ".spaceIds", entry.Key, index);
            index++;
            void Required(string field) => throw Invalid(MapDocumentRefusal.SchemaValueRequired, path + "." + field, surface: surface, patch: entry.Key, index: index);
        }
    }

    static int RequireCount<T>(IReadOnlyList<T>? values, string path)
    {
        int? count = values?.Count;
        if (values is null || count is < 1 or > MaxEntries)
            throw new MapDocumentException(values is null ? MapDocumentRefusal.SchemaValueRequired
                : count < 1 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded, "invalid surface storage page")
            {
                FieldPath = path,
                ActualCount = count,
                ActualInteger = count < 1 ? count : null,
                MinimumInteger = count < 1 ? 1 : null,
                Budget = count > MaxEntries ? "MaxEntries" : null,
                Limit = count > MaxEntries ? MaxEntries : null
            };
        return count!.Value;
    }

    static void Unique<T>(IReadOnlyList<T> values, string path, MapPatchKey patch, int index)
    {
        int distinct = values.Distinct().Count(), count = values.Count;
        if (distinct != count) throw Invalid(MapDocumentRefusal.IdentityDuplicate, path, actualCount: count,
            expectedCount: distinct, surface: patch.SurfaceId, patch: patch, index: index);
    }

    static MapDocumentException Invalid(MapDocumentRefusal reason, string path, long? actualCount = null,
        long? expectedCount = null, string? surface = null, MapPatchKey? patch = null, int? index = null) => new(reason, "invalid surface storage page")
        { FieldPath = path, ActualCount = actualCount, ExpectedCount = expectedCount, SurfaceId = surface, Patch = patch, Index = index };

    sealed class PageOutput : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MapSurfaceStorageLayout.MaxPageBytes - Length)
                throw ByteLimit(Length + buffer.Length);
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > MapSurfaceStorageLayout.MaxPageBytes - Length)
                throw ByteLimit(Length + count);
            base.Write(buffer, offset, count);
        }
        static MapDocumentException ByteLimit(long actual) => new(MapDocumentRefusal.WorkCapacityExceeded, "surface page exceeds 1 MiB")
        { FieldPath = "$", Budget = "MaxPageBytes", ActualByteCount = actual, Limit = MapSurfaceStorageLayout.MaxPageBytes };
    }
}
