using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabClosureRefusalTests
{
    sealed record UnsupportedChild(MapPrefabChildKey ChildKey) : MapPrefabChild(ChildKey);
    sealed record UnsupportedRecord(string RecordId) : MapTopologyRecord(RecordId);

    [Fact]
    public void PartialExpansion_RequiresTheCompleteDocumentAndAcceptsTheSameLoadedTile()
    {
        var fixture = PrefabFixtures.Create("roomless");
        var document = fixture.Document;
        document.Tiles = new(document.TileSize, MapDocumentHash.SchemeVersion, null, [new(new(0, 0), new string('a', 64), false)]);
        string before = JsonSerializer.Serialize(document, MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault()));
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(document, fixture.Assets));
        Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, error.Reason);
        Assert.Equal(document.Id, error.DocumentId);
        Assert.Equal("Expand", error.Operation);
        Assert.Equal(before, JsonSerializer.Serialize(document, MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault())));
        Assert.False(Assert.Single(document.Tiles.Entries).Loaded);
        document.Tiles = new(document.TileSize, MapDocumentHash.SchemeVersion, null, [new(new(0, 0), new string('a', 64), true)]);
        Assert.NotNull(MapPrefabResolver.Expand(document, fixture.Assets));
    }

    [Fact]
    public void ValidationAdapter_PreservesTheTypedFindingAndItsOrderedSnapshot()
    {
        var document = MapPrefabDocumentRefusalTests.Empty();
        document.BuildingIntent = new(1, [], [], [PrefabFixtures.GlowLight() with { Owner = null, Radius = 0 }], []);
        var findings = MapPrefabValidation.ValidateFindings(document, null);
        var finding = Assert.Single(findings);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireDocument(document, null!));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal(finding.FieldPath, error.FieldPath);
        Assert.Equal(finding.EntityId, error.EntityId);
        Assert.Equal(finding.ActualNumber, error.ActualNumber);
        Assert.Equal(findings, error.Findings);
        Assert.Equal(MapPrefabValidation.Validate(document, null), findings.Select(f => f.Message));
        Assert.Throws<NotSupportedException>(() => ((IList<MapDocumentFinding>)findings)[0] = finding);
        document.BuildingIntent = new(1, [], [], [PrefabFixtures.GlowLight() with { Owner = null }], []);
        Assert.Empty(MapPrefabValidation.ValidateFindings(document, null));
    }

    [Theory]
    [InlineData("unclassified")]
    [InlineData("legacy")]
    [InlineData("typed-json")]
    [InlineData("wrapped-aggregate")]
    public void StateReadFailure_RetainsTheCauseWithoutInventingAPropertyPath(string fault)
    {
        MapDocumentFinding[] aggregate =
        [
            new(MapDocumentRefusal.ReferenceMissing, "Missing resource") { ResourceId = "resource" },
            new(MapDocumentRefusal.ValueOutsideRange, "Invalid radius") { FieldPath = "$.radius", ActualNumber = -1 },
        ];
        Exception cause = fault switch
        {
            "legacy" => new MapDocumentException("Unreadable count"),
            "typed-json" => new MapPrefabJsonException(MapDocumentRefusal.SchemaEnumUnsupported, "Unreadable kind")
                { FieldPath = "$.kind", EnumType = typeof(MapPrefabChildKind), ActualText = "Future" },
            "wrapped-aggregate" => new InvalidOperationException("Unreadable count",
                new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Invalid resources")
                    { ResourceId = "resource", Findings = aggregate }),
            _ => new InvalidOperationException("Unreadable count"),
        };
        var document = MapPrefabDocumentRefusalTests.Empty();
        document.Prefabs = new(1, 1, new UnreadableInstances(cause), []);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireDocument(document, null!));
        Assert.Equal(fault == "typed-json" ? MapDocumentRefusal.SchemaEnumUnsupported
            : fault == "wrapped-aggregate" ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.PrefabStateUnreadable, error.Reason);
        if (fault == "typed-json")
        {
            Assert.Equal("$.kind", error.FieldPath);
            Assert.Equal(typeof(MapPrefabChildKind), error.EnumType);
            Assert.Equal("Future", error.ActualText);
        }
        else if (fault == "wrapped-aggregate")
        {
            Assert.Equal("resource", error.ResourceId);
            Assert.Equal(aggregate, MapPrefabValidation.ValidateFindings(document, null));
            Assert.Equal(aggregate, error.Findings);
        }
        else
        {
            Assert.Equal(document.Id, error.DocumentId);
            Assert.Null(error.FieldPath);
        }
        Assert.Same(cause, error.InnerException);
        if (fault != "wrapped-aggregate")
        {
            Assert.Equal(error.Reason, Assert.Single(error.Findings!).Reason);
            Assert.Equal(MapPrefabValidation.Validate(document, null), error.Findings!.Select(f => f.Message));
        }
        document.Prefabs = new(1, 1, [], []);
        MapPrefabValidation.RequireDocument(document, null!);
    }

    [Fact]
    public void UnsupportedChildren_KeepTheirRuntimeTypeAtValidation()
    {
        MapPrefabChildKey key = new(MapPrefabChildKind.Storey, "child");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(new(1, "asset", [new UnsupportedChild(key)])));
        Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal(typeof(UnsupportedChild), error.RecordType);
        Assert.Equal(key, error.Child);
        MapPrefabValidation.RequireAsset(new(1, "asset", [new MapPrefabStoreyChild(key, "Storey")]));
    }

    [Fact]
    public void UnknownTopology_AndUndefinedDescriptorKindsRetainTheirOwnSubjects()
    {
        MapPrefabChildKey key = new(MapPrefabChildKind.Record, "record");
        var child = new MapPrefabRecordChild(key, new UnsupportedRecord("record"), new("floor", 0, 0));
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity));
        Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal(typeof(UnsupportedRecord), error.RecordType);
        Assert.Equal("record", error.RecordId);
        var control = child with { Value = new MapSpaceDoc("record", MapSpaceKind.Room, null, null, [], [], [], []) };
        Assert.Equal(key, MapPrefabReferenceWalker.Child(control, MapPrefabReferenceMap.Identity).Child.Key);

        var document = MapPrefabDocumentRefusalTests.Empty();
        var descriptor = Assert.Throws<MapDocumentException>(() => MapPrefabAttachmentDescriptors.Read(document, (MapPrefabChildKind)99, "subject"));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, descriptor.Reason);
        Assert.Equal(typeof(MapPrefabChildKind), descriptor.EnumType);
        Assert.Equal(99L, descriptor.ActualInteger);
        Assert.Equal("subject", descriptor.EntityId);
        Assert.Null(MapPrefabAttachmentDescriptors.Read(document, MapPrefabChildKind.Storey, "subject"));
    }

    sealed class UnreadableInstances(Exception cause) : IReadOnlyList<MapPrefabInstance>
    {
        public int Count => throw cause;
        public MapPrefabInstance this[int index] => throw cause;
        public IEnumerator<MapPrefabInstance> GetEnumerator() => throw cause;
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
