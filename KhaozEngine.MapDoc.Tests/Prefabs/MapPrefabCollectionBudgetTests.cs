using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

[Collection("prefab-collection-budgets")]
public sealed class MapPrefabCollectionBudgetTests
{
    static readonly string Digest = new('a', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CountFailures_RetainTheValueThatFailedThePredicate(bool child)
    {
        int limit = child ? MapPrefabJson.Limits.MaxChildren : MapPrefabJson.Limits.MaxInstances;
        MapDocumentException error;
        Action control;
        if (child)
        {
            var asset = new MapPrefabAsset(1, "asset", new ChangingCount<MapPrefabChild>(limit + 1));
            error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(asset));
            control = () => MapPrefabValidation.RequireAsset(asset with { Children = [] });
        }
        else
        {
            var document = MapPrefabDocumentRefusalTests.Empty();
            document.Prefabs = new(1, 1, new ChangingCount<MapPrefabInstance>(limit + 1), []);
            error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireDocument(document, null!));
            control = () =>
            {
                document.Prefabs = new(1, 1, [], []);
                MapPrefabValidation.RequireDocument(document, null!);
            };
        }
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal((long)limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        control();
    }
    [Theory]
    [InlineData("instances", "$.prefabs.instances")]
    [InlineData("transfers", "$.prefabs.transfers")]
    [InlineData("attachments", "$.prefabs.attachments")]
    [InlineData("children", "$.children")]
    public void MissingCollections_RetainTheFieldAndAcceptAnEmptyCollection(string field, string path)
    {
        if (field == "children")
        {
            var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireAsset(new(1, "asset", null!)));
            Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
            Assert.Equal(path, error.FieldPath);
            Assert.Equal("asset", error.AssetId);
            MapPrefabValidation.RequireAsset(new(1, "asset", []));
            return;
        }
        var document = MapPrefabDocumentRefusalTests.Empty();
        document.Prefabs = new(1, 1, field == "instances" ? null! : [], field == "transfers" ? null! : [])
        { Attachments = field == "attachments" ? null! : [] };
        var finding = Assert.Single(MapPrefabValidation.ValidateFindings(document, null));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, finding.Reason);
        Assert.Equal(path, finding.FieldPath);
        Assert.Null(finding.ActualCount);
        document.Prefabs = new(1, 1, [], []);
        Assert.Empty(MapPrefabValidation.ValidateFindings(document, null));
    }

    [Theory]
    [InlineData("instances")]
    [InlineData("transfers")]
    [InlineData("attachments")]
    [InlineData("children")]
    public void CollectionBudgets_RunTheRealValidatorAtTheCapAndOneBelow(string field)
    {
        int limit = field switch
        {
            "instances" => MapPrefabJson.Limits.MaxInstances,
            "children" => MapPrefabJson.Limits.MaxChildren,
            _ => MapPrefabJson.Limits.MaxTransfers,
        };
        var document = MapPrefabDocumentRefusalTests.Empty();
        MapPrefabAsset Asset(int count) => new(1, "asset", new GeneratedList<MapPrefabChild>(count,
            i => new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "c" + i), "Storey")));
        MapPrefabSet Set(int count) => new(1, 1,
            field == "instances" ? new GeneratedList<MapPrefabInstance>(count,
                i => new("i" + i, "resource", Digest, new(default, 0, 1), null, new([]), []) { Removed = true }) : [],
            field == "transfers" ? new GeneratedList<MapPrefabBindingTransfer>(count,
                i => new("source", new(new(MapPrefabChildKind.Storey, "c" + i), "w" + i, null, false))) : [])
        {
            Attachments = field == "attachments" ? new GeneratedList<MapPrefabAttachmentTransfer>(count,
                i => new("p" + i, "resource", Digest, MapPrefabAttachmentKind.Support, [], false)) : [],
        };
        void Validate(int count)
        {
            if (field == "children") MapPrefabValidation.RequireAsset(Asset(count));
            else
            {
                document.Prefabs = Set(count);
                MapPrefabValidation.RequireDocument(document, null!);
            }
        }
        var error = Assert.Throws<MapDocumentException>(() => Validate(limit + 1));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(field == "children" ? "$.children" : "$.prefabs." + field, error.FieldPath);
        Assert.Equal(field, error.Budget);
        Assert.Equal((long)limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Validate(limit);
        Validate(limit - 1);
    }

    sealed class GeneratedList<T>(int count, Func<int, T> item) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => index >= 0 && index < count ? item(index) : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<T> GetEnumerator() => Enumerable.Range(0, count).Select(item).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    sealed class ChangingCount<T>(int first) : IReadOnlyList<T>
    {
        bool _first = true;
        public int Count { get { int count = _first ? first : 0; _first = false; return count; } }
        public T this[int index] => throw new InvalidOperationException();
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
