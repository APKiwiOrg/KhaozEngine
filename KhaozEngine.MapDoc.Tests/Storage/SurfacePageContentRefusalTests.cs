using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using KhaozEngine.MapDoc.Spaces;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfacePageContentRefusalTests
{
    static readonly MapSlotRect Covers = new(0, 0, 2, 2);
    static readonly string Digest = new('a', 64);

    [Theory]
    [InlineData("null-list", MapDocumentRefusal.SchemaValueRequired, "$.pages")]
    [InlineData("empty-list", MapDocumentRefusal.ValueOutsideRange, "$.pages")]
    [InlineData("null-page", MapDocumentRefusal.SchemaValueRequired, "$.pages[0]")]
    [InlineData("x-bounds", MapDocumentRefusal.BoundsInvalid, "$.covers")]
    [InlineData("z-bounds", MapDocumentRefusal.BoundsInvalid, "$.covers")]
    [InlineData("outside", MapDocumentRefusal.BoundsInvalid, "$.pages[0].covers")]
    [InlineData("empty-count", MapDocumentRefusal.ValueOutsideRange, "$.pages[0].entryCount")]
    [InlineData("over-count", MapDocumentRefusal.WorkCapacityExceeded, "$.pages[0].entryCount")]
    [InlineData("overlap", MapDocumentRefusal.StorageDirectoryPagesOverlap, "$.pages[1].covers")]
    public void DirectoryContent_SelectsTheFailedRuleBeforeLaterChecks(string fault, MapDocumentRefusal reason, string field)
    {
        MapSlotRect covers = fault == "x-bounds" ? new(0, 0, 0, 2) : fault == "z-bounds" ? new(0, 0, 2, 0) : Covers;
        var page = new MapIndexPageRef(fault == "outside" ? new(2, 0, 3, 1) : new(0, 0, 1, 1), Digest,
            fault == "empty-count" ? 0 : fault == "over-count" ? 257 : 1);
        IReadOnlyList<MapIndexPageRef>? pages = fault switch
        {
            "null-list" => null, "empty-list" => [], "null-page" => [null!], "overlap" => [page, page], _ => [page],
        };
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.EncodeDirectory("r", covers, pages!));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (fault == "over-count") { Assert.Equal(257, error.ActualCount); Assert.Equal(256, error.Limit); }
        if (fault == "empty-count") { Assert.Equal(0, error.ActualInteger); Assert.Equal(1, error.MinimumInteger); }
        if (fault == "overlap") { Assert.Equal(1, error.Index); Assert.Equal(0, error.OtherIndex); }
        MapSlotRect controlCovers = fault == "x-bounds" ? new(0, 0, 1, 2) : fault == "z-bounds" ? new(0, 0, 2, 1) : Covers;
        var control = new MapIndexPageRef(fault == "outside" ? new(1, 0, 2, 1) : new(0, 0, 1, 1), Digest, fault == "over-count" ? 256 : 1);
        MapIndexPageRef[] controls = fault == "overlap" ? [control, control with { Covers = new(1, 0, 2, 1) }] : [control];
        byte[] accepted = MapSurfacePages.EncodeDirectory("r", controlCovers, controls);
        Assert.Equal(controls.Length, MapSurfacePages.DecodeDirectory(accepted, new("r", controlCovers, Digest)).Count);
    }

    [Fact]
    public void DirectoryReference_IdentifiesItsRequiredSurface()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.Validate(new("", Covers, Digest)));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$.surfaceId", error.FieldPath);
        MapSurfacePages.Validate(new("r", Covers, Digest));
    }

    [Fact]
    public void DirectoryPageCount_AdmitsExactlyTheInclusiveLimit()
    {
        var pages = Enumerable.Range(0, 257).Select(i => new MapIndexPageRef(new(i, 0, i + 1, 1), Digest, 1)).ToArray();
        var covers = new MapSlotRect(0, 0, 257, 1);
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.EncodeDirectory("r", covers, pages));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("MaxEntries", error.Budget);
        Assert.Equal(257, error.ActualCount);
        Assert.Equal(256, error.Limit);
        byte[] bytes = MapSurfacePages.EncodeDirectory("r", covers, pages[..256]);
        Assert.Equal(256, MapSurfacePages.DecodeDirectory(bytes, new("r", covers, Digest)).Count);
    }

    [Theory]
    [InlineData("null-list", MapDocumentRefusal.SchemaValueRequired, "$.entries")]
    [InlineData("empty-list", MapDocumentRefusal.ValueOutsideRange, "$.entries")]
    [InlineData("null-entry", MapDocumentRefusal.SchemaValueRequired, "$.entries[0]")]
    [InlineData("surface", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.entries[0].key.surfaceId")]
    [InlineData("outside", MapDocumentRefusal.BoundsInvalid, "$.entries[0].key")]
    [InlineData("order", MapDocumentRefusal.OrderInvalid, "$.entries[1].key")]
    [InlineData("duplicate-key", MapDocumentRefusal.OrderInvalid, "$.entries[1].key")]
    [InlineData("min-x", MapDocumentRefusal.BoundsInvalid, "$.entries[0].cells")]
    [InlineData("min-z", MapDocumentRefusal.BoundsInvalid, "$.entries[0].cells")]
    [InlineData("max-x", MapDocumentRefusal.BoundsInvalid, "$.entries[0].cells")]
    [InlineData("max-z", MapDocumentRefusal.BoundsInvalid, "$.entries[0].cells")]
    [InlineData("empty-x", MapDocumentRefusal.BoundsInvalid, "$.entries[0].cells")]
    [InlineData("empty-z", MapDocumentRefusal.BoundsInvalid, "$.entries[0].cells")]
    [InlineData("height", MapDocumentRefusal.BoundsInvalid, "$.entries[0].maxHeightUnits")]
    [InlineData("null-dependencies", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].dependencies")]
    [InlineData("null-records", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].recordIds")]
    [InlineData("null-incidents", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].incidentRecords")]
    [InlineData("null-spaces", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].spaceIds")]
    [InlineData("blank-dependency", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].dependencies")]
    [InlineData("blank-record", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].recordIds")]
    [InlineData("blank-space", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].spaceIds")]
    [InlineData("blank-incident", MapDocumentRefusal.SchemaValueRequired, "$.entries[0].incidentRecords")]
    [InlineData("duplicate-dependency", MapDocumentRefusal.IdentityDuplicate, "$.entries[0].dependencies")]
    [InlineData("duplicate-record", MapDocumentRefusal.IdentityDuplicate, "$.entries[0].recordIds")]
    [InlineData("duplicate-incident", MapDocumentRefusal.IdentityDuplicate, "$.entries[0].incidentRecords")]
    [InlineData("duplicate-space", MapDocumentRefusal.IdentityDuplicate, "$.entries[0].spaceIds")]
    public void IndexContent_IdentifiesTheFirstInvalidEntryRule(string fault, MapDocumentRefusal reason, string field)
    {
        MapSurfaceIndexEntry entry = Entry(0);
        entry = fault switch
        {
            "surface" => entry with { Key = new("other", 0, 0) }, "outside" => Entry(2),
            "min-x" => entry with { Cells = new(-1, 0, 1, 1) }, "min-z" => entry with { Cells = new(0, -1, 1, 1) },
            "max-x" => entry with { Cells = new(0, 0, 65, 1) }, "max-z" => entry with { Cells = new(0, 0, 1, 65) },
            "empty-x" => entry with { Cells = new(0, 0, 0, 1) }, "empty-z" => entry with { Cells = new(0, 0, 1, 0) },
            "height" => entry with { MinHeightUnits = 1 },
            "null-dependencies" => entry with { Dependencies = null! }, "null-records" => entry with { RecordIds = null! },
            "null-incidents" => entry with { IncidentRecords = null! }, "null-spaces" => entry with { SpaceIds = null! },
            "blank-dependency" => entry with { Dependencies = [new("", 0, 0)] },
            "blank-record" => entry with { RecordIds = [""] }, "blank-space" => entry with { SpaceIds = [""] },
            "blank-incident" => entry with { IncidentRecords = [new("", entry.Key)] },
            "duplicate-dependency" => entry with { Dependencies = [entry.Key, entry.Key] },
            "duplicate-record" => entry with { RecordIds = ["a", "a"] },
            "duplicate-incident" => entry with { IncidentRecords = [new("a", entry.Key), new("a", entry.Key)] },
            "duplicate-space" => entry with { SpaceIds = ["a", "a"] }, _ => entry,
        };
        IReadOnlyList<MapSurfaceIndexEntry>? entries = fault switch
        {
            "null-list" => null, "empty-list" => [], "null-entry" => [null!], "order" => [Entry(1), Entry(0)],
            "duplicate-key" => [entry, entry], _ => [entry],
        };
        var error = Assert.Throws<MapDocumentException>(() => MapSurfacePages.EncodeIndex("r", Covers, entries!));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        MapSurfaceIndexEntry control = fault switch
        {
            "max-x" => Entry(0) with { Cells = new(0, 0, 64, 1) }, "max-z" => Entry(0) with { Cells = new(0, 0, 1, 64) },
            "outside" => Entry(1),
            "blank-dependency" or "duplicate-dependency" => Entry(0) with { Dependencies = [new("r", 0, 0)] },
            "blank-record" or "duplicate-record" => Entry(0) with { RecordIds = ["a"] },
            "blank-space" or "duplicate-space" => Entry(0) with { SpaceIds = ["a"] },
            "blank-incident" or "duplicate-incident" => Entry(0) with { IncidentRecords = [new("a", new("r", 0, 0))] },
            _ => Entry(0),
        };
        MapSurfaceIndexEntry[] controls = fault is "order" or "duplicate-key" ? [control, Entry(1)] : [control];
        byte[] accepted = MapSurfacePages.EncodeIndex("r", Covers, controls);
        Assert.Equal(controls.Length, MapSurfacePages.DecodeIndex(accepted, "r", new(Covers, Digest, controls.Length)).Count);
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("entries")]
    [InlineData("dependencies")]
    [InlineData("recordIds")]
    [InlineData("incidentRecords")]
    [InlineData("spaceIds")]
    public void PageArrays_IdentifyTheirOwnJsonField(string field)
    {
        bool directory = field == "pages";
        byte[] valid = directory ? MapSurfacePages.EncodeDirectory("r", Covers, [new(Covers, Digest, 1)])
            : MapSurfacePages.EncodeIndex("r", Covers, [Entry(0)]);
        var root = JsonNode.Parse(valid)!.AsObject();
        if (field is "pages" or "entries") root[field] = false;
        else root["entries"]![0]![field] = false;
        var error = Assert.Throws<MapDocumentException>(() => Decode(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Equal(MapDocumentRefusal.SchemaArrayRequired, error.Reason);
        Assert.Equal(field is "pages" or "entries" ? "$." + field : "$.entries[0]." + field, error.FieldPath);
        Decode(valid);
        void Decode(byte[] bytes)
        {
            if (directory) Assert.Single(MapSurfacePages.DecodeDirectory(bytes, new("r", Covers, Digest)));
            else Assert.Single(MapSurfacePages.DecodeIndex(bytes, "r", new(Covers, Digest, 1)));
        }
    }

    [Fact]
    public void PageWriterByteCapacity_RefusesBeforeAnySavedFileChanges()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        MapSurfacePatch patch = Assert.Single(doc.Surfaces.Patches.Values);
        Array.Fill(patch.Heights, 0);
        patch.Records.Add(Space("x"));
        string directory = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            int overhead = Assert.Single(MapSurfaceTiledStore.Prepare(doc, directory, null).Files, f => f.Kind == 'i').Bytes.Length - 2;
            // A space id occurs once in recordIds and once in spaceIds. A two-digit maximum height fixes odd padding.
            if ((MapSurfaceStorageLayout.MaxPageBytes - overhead) % 2 != 0)
            {
                patch.Heights[^1] = 10;
                overhead++;
            }
            int idLength = (MapSurfaceStorageLayout.MaxPageBytes - overhead) / 2;
            patch.Records[0] = Space(new string('x', idLength + 1));
            doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("new-tree", 0, 0));
            var saved = AssertFixtures.Files(directory);
            Assert.NotEmpty(saved);
            var error = Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, directory));
            Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
            Assert.Equal("MaxPageBytes", error.Budget);
            Assert.Equal(MapSurfaceStorageLayout.MaxPageBytes, error.Limit);
            Assert.True(error.ActualByteCount > error.Limit);
            Assert.Equal(saved, AssertFixtures.Files(directory));
            patch.Records[0] = Space(new string('x', idLength));
            MapDocumentFile.SaveTiled(doc, directory);
            string page = Assert.Single(Directory.GetFiles(Path.Combine(directory, "tiles", "surfaces", "i"), "*.json"));
            Assert.Equal(error.Limit, new FileInfo(page).Length);
            Assert.Empty(MapDocumentFile.VerifyTiled(directory));
            patch.Records[0] = Space(new string('x', idLength - 1));
            MapDocumentFile.SaveTiled(doc, directory);
            Assert.Empty(MapDocumentFile.VerifyTiled(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
        static MapSpaceDoc Space(string id) => new(id, MapSpaceKind.Exterior, null, null, [], [], [], []);
    }

    [Theory]
    [InlineData("pages", 256)]
    [InlineData("entries", 256)]
    [InlineData("recordIds", 43690)]
    public void DecodedArrayCapacity_AdmitsTheRealOperationAtItsInclusiveLimit(string field, int limit)
    {
        var covers = new MapSlotRect(0, 0, 257, 1);
        byte[] valid = field switch
        {
            "pages" => MapSurfacePages.EncodeDirectory("r", covers, Enumerable.Range(0, limit)
                .Select(i => new MapIndexPageRef(new(i, 0, i + 1, 1), Digest, 1)).ToArray()),
            "entries" => MapSurfacePages.EncodeIndex("r", covers, Enumerable.Range(0, limit).Select(Entry).ToArray()),
            _ => MapSurfacePages.EncodeIndex("r", covers, [Entry(0) with { RecordIds = Enumerable.Range(0, limit).Select(i => "r" + i).ToArray() }]),
        };
        JsonObject root = JsonNode.Parse(valid)!.AsObject();
        if (field == "pages")
        {
            var extra = root["pages"]![0]!.DeepClone();
            extra["covers"]!["minX"] = 256; extra["covers"]!["maxXExclusive"] = 257;
            root["pages"]!.AsArray().Add(extra);
        }
        else if (field == "entries")
        {
            var extra = JsonNode.Parse(MapSurfacePages.EncodeIndex("r", covers, [Entry(256)]))!["entries"]![0]!.DeepClone();
            root["entries"]!.AsArray().Add(extra);
        }
        else root["entries"]![0]!["recordIds"]!.AsArray().Add("r" + limit);
        var error = Assert.Throws<MapDocumentException>(() => Decode(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(field == "recordIds" ? "$.entries[0].recordIds" : "$." + field, error.FieldPath);
        Assert.Equal(field == "recordIds" ? "MaxMetadataEntries" : "MaxEntries", error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Decode(valid);
        void Decode(byte[] bytes)
        {
            if (field == "pages") Assert.Equal(256, MapSurfacePages.DecodeDirectory(bytes, new("r", covers, Digest)).Count);
            else
            {
                var entries = MapSurfacePages.DecodeIndex(bytes, "r", new(covers, Digest, field == "entries" ? 256 : 1));
                if (field == "entries") Assert.Equal(256, entries.Count);
                else Assert.Equal(43690, Assert.Single(entries).RecordIds.Count);
            }
        }
    }

    static MapSurfaceIndexEntry Entry(int slot) => new(new("r", slot, 0), new(slot * 64, 0, slot * 64 + 1, 1),
        0, 0, Digest, Digest, [], [], [], [], false);
}
