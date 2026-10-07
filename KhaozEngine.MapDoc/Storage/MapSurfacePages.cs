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
        Members(json.RootElement, "surfaceId", "covers", "pages");
        RectangleMembers(json.RootElement.GetProperty("covers"));
        JsonElement pages = json.RootElement.GetProperty("pages");
        CheckArray(pages, MaxEntries);
        foreach (JsonElement pageRef in pages.EnumerateArray())
        {
            Members(pageRef, "covers", "sha256", "entryCount"); RectangleMembers(pageRef.GetProperty("covers"));
        }
        DirectoryPage page = json.RootElement.Deserialize<DirectoryPage>(Options) ?? throw Invalid();
        if (page.SurfaceId != expected.SurfaceId || page.Covers != expected.Covers) throw Invalid();
        ValidatePages(page.Covers, page.Pages);
        return Array.AsReadOnly(page.Pages.ToArray());
    }
    internal static IReadOnlyList<MapSurfaceIndexEntry> DecodeIndex(byte[] bytes, string surface, MapIndexPageRef expected)
    {
        using JsonDocument json = Parse(bytes);
        Members(json.RootElement, "surfaceId", "covers", "entries");
        RectangleMembers(json.RootElement.GetProperty("covers"));
        JsonElement entries = json.RootElement.GetProperty("entries");
        CheckArray(entries, MaxEntries);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            Members(entry, "key", "cells", "minHeightUnits", "maxHeightUnits", "payloadSha256", "semanticSha256",
                "dependencies", "recordIds", "incidentRecords", "spaceIds");
            foreach (string name in new[] { "dependencies", "recordIds", "incidentRecords", "spaceIds" })
                CheckArray(entry.GetProperty(name), MapSurfaceStorageLayout.MaxPageBytes / 24);
            RectangleMembers(entry.GetProperty("cells"));
            KeyMembers(entry.GetProperty("key"));
            foreach (JsonElement key in entry.GetProperty("dependencies").EnumerateArray()) KeyMembers(key);
            foreach (JsonElement incident in entry.GetProperty("incidentRecords").EnumerateArray())
            {
                Members(incident, "id", "anchor"); KeyMembers(incident.GetProperty("anchor"));
            }
        }
        IndexPage page = json.RootElement.Deserialize<IndexPage>(Options) ?? throw Invalid();
        if (page.SurfaceId != surface || page.Covers != expected.Covers || page.Entries.Count != expected.EntryCount) throw Invalid();
        ValidateEntries(surface, page.Covers, page.Entries);
        return Array.AsReadOnly(page.Entries.Select(e => e with
        {
            Dependencies = Array.AsReadOnly(e.Dependencies.ToArray()), RecordIds = Array.AsReadOnly(e.RecordIds.ToArray()),
            IncidentRecords = Array.AsReadOnly(e.IncidentRecords.ToArray()), SpaceIds = Array.AsReadOnly(e.SpaceIds.ToArray()), Loaded = false,
        }).ToArray());
    }
    static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MapSurfaceStorageLayout.MaxPageBytes) throw Invalid();
        JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { ClosedValues(json.RootElement); return json; }
        catch { json.Dispose(); throw; }
    }
    static void ClosedValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid();
                ClosedValues(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) ClosedValues(item);
        else if (value.ValueKind == JsonValueKind.Null) throw Invalid();
    }
    static void Members(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length) throw Invalid();
        foreach (string name in names) if (!root.TryGetProperty(name, out _)) throw Invalid();
    }
    static void RectangleMembers(JsonElement value) => Members(value, "minX", "minZ", "maxXExclusive", "maxZExclusive");
    static void KeyMembers(JsonElement value) => Members(value, "surfaceId", "slotX", "slotZ");
    static void CheckArray(JsonElement array, int limit)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > limit) throw Invalid();
    }
    internal static void Validate(MapDirectoryPageRef page)
    {
        if (string.IsNullOrWhiteSpace(page.SurfaceId)) throw Invalid();
        ValidateRect(page.Covers); MapSurfaceStorageLayout.RequireDigest(page.Sha256);
    }
    internal static void ValidateRect(MapSlotRect rect)
    {
        if (rect.MinX >= rect.MaxXExclusive || rect.MinZ >= rect.MaxZExclusive) throw Invalid();
    }
    static bool Within(MapSlotRect inner, MapSlotRect outer) => inner.MinX >= outer.MinX && inner.MinZ >= outer.MinZ &&
        inner.MaxXExclusive <= outer.MaxXExclusive && inner.MaxZExclusive <= outer.MaxZExclusive;
    static void ValidatePages(MapSlotRect covers, IReadOnlyList<MapIndexPageRef> pages)
    {
        ValidateRect(covers);
        if (pages is null || pages.Count is < 1 or > MaxEntries) throw Invalid();
        for (int i = 0; i < pages.Count; i++)
        {
            MapIndexPageRef page = pages[i] ?? throw Invalid();
            ValidateRect(page.Covers); MapSurfaceStorageLayout.RequireDigest(page.Sha256);
            if (!Within(page.Covers, covers) || page.EntryCount is < 1 or > MaxEntries) throw Invalid();
            for (int j = 0; j < i; j++) if (pages[j].Covers.Overlaps(page.Covers)) throw Invalid();
        }
    }
    static void ValidateEntries(string surface, MapSlotRect covers, IReadOnlyList<MapSurfaceIndexEntry> entries)
    {
        ValidateRect(covers);
        if (entries is null || entries.Count is < 1 or > MaxEntries) throw Invalid();
        MapPatchKey? previous = null;
        foreach (MapSurfaceIndexEntry entry in entries)
        {
            if (entry is null || entry.Key.SurfaceId != surface || !covers.Contains(entry.Key.SlotX, entry.Key.SlotZ) ||
                (previous is { } key && key.CompareTo(entry.Key) >= 0)) throw Invalid();
            previous = entry.Key;
            MapSurfaceStorageLayout.RequireDigest(entry.PayloadSha256); MapSurfaceStorageLayout.RequireDigest(entry.SemanticSha256);
            MapCellRect cells = entry.Cells;
            Int128 x = (Int128)entry.Key.SlotX * 64, z = (Int128)entry.Key.SlotZ * 64;
            if (cells.MinX < x || cells.MinZ < z || cells.MaxXExclusive > x + 64 || cells.MaxZExclusive > z + 64 ||
                cells.MinX >= cells.MaxXExclusive || cells.MinZ >= cells.MaxZExclusive || entry.MinHeightUnits > entry.MaxHeightUnits ||
                entry.Dependencies is null || entry.RecordIds is null || entry.IncidentRecords is null || entry.SpaceIds is null) throw Invalid();
            if (entry.Dependencies.Any(k => string.IsNullOrWhiteSpace(k.SurfaceId)) ||
                entry.RecordIds.Concat(entry.SpaceIds).Any(string.IsNullOrWhiteSpace) ||
                entry.IncidentRecords.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Anchor.SurfaceId))) throw Invalid();
            if (entry.Dependencies.Distinct().Count() != entry.Dependencies.Count || entry.RecordIds.Distinct().Count() != entry.RecordIds.Count ||
                entry.IncidentRecords.Distinct().Count() != entry.IncidentRecords.Count || entry.SpaceIds.Distinct().Count() != entry.SpaceIds.Count) throw Invalid();
        }
    }
    static MapDocumentException Invalid() => new("invalid surface storage page");

    sealed class PageOutput : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MapSurfaceStorageLayout.MaxPageBytes - Length)
                throw new MapDocumentException("surface page exceeds 1 MiB");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > MapSurfaceStorageLayout.MaxPageBytes - Length)
                throw new MapDocumentException("surface page exceeds 1 MiB");
            base.Write(buffer, offset, count);
        }
    }
}
