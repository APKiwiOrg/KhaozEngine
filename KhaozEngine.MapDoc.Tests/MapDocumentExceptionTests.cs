using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Support;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentExceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_constructors_keep_the_cause_and_leave_the_reason_unset(bool withInner)
    {
        var inner = new InvalidOperationException();
        var refusal = withInner ? new MapDocumentException("consumer refusal", inner) : new MapDocumentException("consumer refusal");
        Assert.Null(refusal.Reason);
        Assert.Same(withInner ? inner : null, refusal.InnerException);
    }

    [Fact]
    public void Structured_constructor_keeps_the_reason_and_original_cause()
    {
        var inner = new InvalidOperationException();
        var refusal = new MapDocumentException(MapDocumentRefusal.ResolverContractMismatch, "refused resolver", inner);
        Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, refusal.Reason);
        Assert.Same(inner, refusal.InnerException);
    }

    [Fact]
    public void Findings_are_an_ordered_owned_read_only_snapshot()
    {
        var first = new MapDocumentFinding(MapDocumentRefusal.ResolverContractMismatch, "first") { ResolverVersion = 1 };
        var second = first with { ResolverVersion = 3 };
        var source = new List<MapDocumentFinding> { first, second };
        var refusal = new MapDocumentException(first.Reason, "refused document") { Findings = source };
        source.Reverse();
        source.Clear();
        Assert.NotNull(refusal.Findings);
        Assert.Equal(new[] { first, second }, refusal.Findings);
        if (refusal.Findings is IList<MapDocumentFinding> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = second);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal(new[] { first, second }, refusal.Findings);
    }

    [Fact]
    public void Resource_marker_and_source_ids_are_owned_read_only_snapshots()
    {
        string[] source = ["a", "b"];
        var refusal = new MapDocumentException(MapDocumentRefusal.AssetDependencyCycle, "cycle") { ResourceIds = source };
        var finding = new MapDocumentFinding(MapDocumentRefusal.AssetDependencyCycle, "cycle") { ResourceIds = source };
        var choices = new MapDocumentException(MapDocumentRefusal.MarkerAdoptionChoiceSetMismatch, "choices") { SourceIds = source };
        var choiceFinding = new MapDocumentFinding(MapDocumentRefusal.MarkerAdoptionChoiceSetMismatch, "choices") { SourceIds = source };
        var collision = new MapDocumentException(MapDocumentRefusal.MarkerNameDuplicate, "collision") { MarkerIds = source };
        var collisionFinding = new MapDocumentFinding(MapDocumentRefusal.MarkerNameDuplicate, "collision") { MarkerIds = source };
        source[0] = "changed";
        Assert.Equal(new[] { "a", "b" }, refusal.ResourceIds);
        if (refusal.ResourceIds is IList<string> list)
            Assert.Throws<NotSupportedException>(() => list[0] = "changed");
        Assert.Equal(new[] { "a", "b" }, refusal.ResourceIds);
        Assert.Equal(new[] { "a", "b" }, finding.ResourceIds);
        if (finding.ResourceIds is IList<string> findingList)
            Assert.Throws<NotSupportedException>(() => findingList[0] = "changed");
        Assert.Equal(new[] { "a", "b" }, finding.ResourceIds);
        Assert.Equal(new[] { "a", "b" }, choices.SourceIds);
        Assert.Equal(new[] { "a", "b" }, choiceFinding.SourceIds);
        if (choices.SourceIds is IList<string> choiceList) Assert.Throws<NotSupportedException>(() => choiceList[0] = "changed");
        if (choiceFinding.SourceIds is IList<string> choiceFindingList) Assert.Throws<NotSupportedException>(() => choiceFindingList[0] = "changed");
        Assert.Equal(new[] { "a", "b" }, choices.SourceIds);
        Assert.Equal(new[] { "a", "b" }, choiceFinding.SourceIds);
        Assert.Equal(new[] { "a", "b" }, collision.MarkerIds);
        Assert.Equal(new[] { "a", "b" }, collisionFinding.MarkerIds);
        if (collision.MarkerIds is IList<string> markerList) Assert.Throws<NotSupportedException>(() => markerList[0] = "changed");
        if (collisionFinding.MarkerIds is IList<string> markerFindingList) Assert.Throws<NotSupportedException>(() => markerFindingList[0] = "changed");
        Assert.Equal(new[] { "a", "b" }, collision.MarkerIds);
        Assert.Equal(new[] { "a", "b" }, collisionFinding.MarkerIds);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void Source_context_preserves_every_identifier_and_the_original_refusal(bool legacy, bool throughJson, bool withoutSource)
    {
        PropertyInfo[] identifiers = typeof(MapDocumentException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanWrite).ToArray();
        Assert.Contains(identifiers, p => p.Name == nameof(MapDocumentException.SourceName));
        var refusal = legacy ? new MapDocumentException("consumer refusal")
            : new MapDocumentException(MapDocumentRefusal.ResolverContractMismatch, "resolver refusal");
        for (int i = 0; i < identifiers.Length; i++) identifiers[i].SetValue(refusal, Sample(identifiers[i], i));

        MapDocumentException named = throughJson
            ? MapDocumentJsonErrors.Wrap(new JsonException("converter failure", new InvalidOperationException("nested", refusal)),
                withoutSource ? null : "map.json", "map.json: converter failure", resourceId: "outer-prefab")
            : refusal.WithSourceName("map.json");

        Assert.Equal(refusal.Reason, named.Reason);
        Assert.Same(refusal, named.InnerException);
        Assert.Equal(withoutSource ? refusal.SourceName : "map.json", named.SourceName);
        Assert.Empty(identifiers.Where(p => p.Name != nameof(MapDocumentException.SourceName) &&
            !Matches(p.GetValue(refusal), p.GetValue(named))).Select(p => p.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validation_finding_projection_preserves_every_identifier_and_leaves_legacy_refusals_untyped(bool legacy)
    {
        PropertyInfo[] identifiers = typeof(MapDocumentException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanWrite && p.Name != nameof(MapDocumentException.Findings)).ToArray();
        Assert.NotEmpty(identifiers);
        Assert.Empty(identifiers.Where(p => typeof(MapDocumentFinding).GetProperty(p.Name) is null).Select(p => p.Name));
        var refusal = legacy ? new MapDocumentException("consumer refusal")
            : new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, "validation refusal");
        for (int i = 0; i < identifiers.Length; i++) identifiers[i].SetValue(refusal, Sample(identifiers[i], i));
        if (legacy) Assert.Null(MapDocumentFinding.FromException(refusal));
        else
        {
            var finding = Assert.IsType<MapDocumentFinding>(MapDocumentFinding.FromException(refusal));
            Assert.Equal(refusal.Reason, finding.Reason);
            Assert.Empty(identifiers.Where(p => !Matches(p.GetValue(refusal),
                typeof(MapDocumentFinding).GetProperty(p.Name)!.GetValue(finding))).Select(p => p.Name));
            var cause = new ArithmeticException("original cause");
            MapDocumentException restored = MapDocumentRefusalFactory.FromFinding(finding, "forwarded finding", cause);
            Assert.Equal(refusal.Reason, restored.Reason);
            Assert.Same(cause, restored.InnerException);
            Assert.Empty(identifiers.Where(p => !Matches(p.GetValue(refusal), p.GetValue(restored))).Select(p => p.Name));
        }
        Assert.NotNull(MapDocumentFinding.FromException(new(MapDocumentRefusal.IdentityDuplicate, "accepted typed projection")));
    }

    static object Sample(PropertyInfo property, int index)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (property.Name == nameof(MapDocumentException.MarkerId)) return "marker-owner";
        if (property.Name == nameof(MapDocumentException.SourceId)) return "spawn-source";
        if (property.Name == nameof(MapDocumentException.MarkerName)) return "arrival";
        if (property.Name == nameof(MapDocumentException.OwnerId)) return "terrain-owner";
        if (property.Name == nameof(MapDocumentException.Operation)) return "Prepare";
        if (property.Name == nameof(MapDocumentException.Numerator)) return 2;
        if (property.Name == nameof(MapDocumentException.Denominator)) return 200;
        if (property.Name == nameof(MapDocumentException.CellX)) return 64;
        if (property.Name == nameof(MapDocumentException.CellZ)) return -1;
        if (property.Name == nameof(MapDocumentException.SlotCell)) return 65;
        if (property.Name == nameof(MapDocumentException.RelatedSlotCell)) return 66;
        if (property.Name == nameof(MapDocumentException.Edge)) return MapCellEdge.West;
        if (property.Name == nameof(MapDocumentException.PreviousInteger)) return 17L;
        if (property.Name == nameof(MapDocumentException.ActualText)) return "future";
        if (property.Name == nameof(MapDocumentException.PolicyId)) return "future-policy";
        if (property.Name == nameof(MapDocumentException.ExpectedPolicyId)) return "legacy-tile";
        if (property.Name == nameof(MapDocumentException.ExpectedPatch)) return new MapPatchKey("expected", 2, -3);
        if (property.Name == nameof(MapDocumentException.ExpectedSnapshot)) return "expected-snapshot";
        if (property.Name == nameof(MapDocumentException.ActualSnapshot)) return "actual-snapshot";
        if (property.Name == nameof(MapDocumentException.RelatedRecordId)) return "parent-record";
        if (property.Name == nameof(MapDocumentException.SourceType)) return typeof(KhaozEngine.MapDoc.Storage.MapDocumentSurfaceSource);
        if (property.Name == nameof(MapDocumentException.RecordType)) return typeof(MapBoundaryChain);
        if (property.Name == nameof(MapDocumentException.ExpectedRecordType)) return typeof(MapSpaceFootprint);
        if (property.Name == nameof(MapDocumentException.Budget)) return "CornerDependencies";
        if (type == typeof(KhaozEngine.MapDoc.Storage.MapPatchStatus)) return KhaozEngine.MapDoc.Storage.MapPatchStatus.Corrupt;
        if (type == typeof(KhaozEngine.MapDoc.Storage.MapAcquireStatus)) return KhaozEngine.MapDoc.Storage.MapAcquireStatus.CapacityExceeded;
        if (type == typeof(Type)) return typeof(MapLocalLightDoc);
        if (type == typeof(string)) return property.Name;
        if (type == typeof(int)) return 1000 + index;
        if (type == typeof(long)) return 3000L + index;
        if (type == typeof(double)) return property.Name switch
        {
            nameof(MapDocumentException.PositionX) => 1_000_001d,
            nameof(MapDocumentException.PositionY) => -1_000_000d,
            nameof(MapDocumentException.PositionZ) => 0.25d,
            _ => 4000.5 + index,
        };
        if (type == typeof(KhaozEngine.MapDoc.Editing.MapCellConversionClass)) return KhaozEngine.MapDoc.Editing.MapCellConversionClass.UnsupportedEncoding;
        if (type == typeof(MapPresencePolicy)) return MapPresencePolicy.LegacyTileWorld;
        if (type == typeof(MapLatticeAddress)) return MapLatticeAddress.Create(1, -2, 3);
        if (type == typeof(MapFoliageReadStatus)) return MapFoliageReadStatus.Unavailable;
        if (type == typeof(MapFoliageStatus)) return MapFoliageStatus.NotRepresentable;
        if (type == typeof(MapFoliageChunkKey)) return new MapFoliageChunkKey(-3, index);
        if (type == typeof(IReadOnlyList<int>)) return new[] { 1, 5 };
        if (type == typeof(IReadOnlyList<string>)) return new[] { "a", "b" };
        if (type == typeof(MapTileCoord)) return new MapTileCoord(-7, index);
        if (type == typeof(MapPrefabReferenceScope)) return (MapPrefabReferenceScope)(8000 + index);
        if (type == typeof(MapPrefabAttachmentKind)) return (MapPrefabAttachmentKind)(7000 + index);
        if (type == typeof(MapPrefabChildKind)) return (MapPrefabChildKind)(6000 + index);
        if (type == typeof(MapPrefabChildKey)) return new MapPrefabChildKey(MapPrefabChildKind.Record, property.Name);
        if (type == typeof(MapPatchKey)) return new MapPatchKey(property.Name, -2, index);
        if (type == typeof(MapEffectiveTransform)) return new MapEffectiveTransform(index, 2, 3, 0, 1);
        if (type == typeof(MapResourceKind)) return (MapResourceKind)(5000 + index);
        if (type == typeof(MapDocumentForm)) return MapDocumentForm.Tiled;
        if (type == typeof(MapSupportStatus)) return MapSupportStatus.MissingGeometry;
        if (type == typeof(MapSupportRecipe)) return (MapSupportRecipe)(2000 + index);
        if (type == typeof(IReadOnlyList<MapDocumentFinding>))
            return new[] { new MapDocumentFinding(MapDocumentRefusal.ResolverContractMismatch, property.Name) { ResolverVersion = index } };
        throw new InvalidOperationException($"Add a sample for identifier {property.Name} of type {property.PropertyType}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foliage_json_wrappers_preserve_every_codec_identifier(bool nested)
    {
        PropertyInfo[] identifiers = typeof(MapFoliageJsonException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanWrite).ToArray();
        Assert.Contains(identifiers, p => p.Name == nameof(MapFoliageJsonException.FieldPath));
        var original = new MapDocumentException(MapDocumentRefusal.SchemaArrayLengthMismatch, "density refused");
        var json = new MapFoliageJsonException(MapDocumentRefusal.SchemaArrayLengthMismatch, "density refused");
        for (int i = 0; i < identifiers.Length; i++)
        {
            object value = Sample(identifiers[i], i);
            identifiers[i].SetValue(json, value);
            typeof(MapDocumentException).GetProperty(identifiers[i].Name)!.SetValue(original, value);
        }
        MapFoliageJsonException converted = MapFoliageJsonException.FromDocument(original);
        Assert.Equal(original.Reason, converted.Reason);
        Assert.Same(original, converted.InnerException);
        Assert.Empty(identifiers.Where(p => !Matches(p.GetValue(json), p.GetValue(converted))).Select(p => p.Name));
        MapDocumentException outer = MapDocumentJsonErrors.Wrap(nested ? new JsonException("outer JSON", json) : json,
            "map.json", "document refused");
        Assert.Equal(json.Reason, outer.Reason);
        Assert.Same(json, outer.InnerException);
        Assert.Empty(identifiers.Where(p => !Matches(p.GetValue(json),
            typeof(MapDocumentException).GetProperty(p.Name)!.GetValue(outer))).Select(p => p.Name));
    }

    [Fact]
    public void A_json_wrapper_keeps_the_context_of_a_message_only_inner_refusal()
    {
        var tile = new MapTileCoord(1, 2);
        var consumer = new MapDocumentException("consumer refusal") { Path = "inner.json", FieldPath = "$.custom", ResourceId = "inner", Tile = tile, SourceName = "inner-source" };
        MapDocumentException outer = MapDocumentJsonErrors.Wrap(new JsonException("converter failure", consumer), null, "converter failure", path: "outer.json", resourceId: "outer");
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, outer.Reason);
        Assert.Equal("inner.json", outer.Path);
        Assert.Equal("$.custom", outer.FieldPath);
        Assert.Equal("inner", outer.ResourceId);
        Assert.Equal(tile, outer.Tile);
        Assert.Equal("inner-source", outer.SourceName);

        // An inner refusal with no context leaves the wrapper's own context in place.
        MapDocumentException plain = MapDocumentJsonErrors.Wrap(new JsonException("converter failure", new MapDocumentException("consumer refusal")),
            "map.json", "converter failure", path: "outer.json", resourceId: "outer");
        Assert.Equal(MapDocumentRefusal.SchemaMalformed, plain.Reason);
        Assert.Equal("outer.json", plain.Path);
        Assert.Equal("outer", plain.ResourceId);
        Assert.Equal("map.json", plain.SourceName);
        Assert.Null(plain.FieldPath);
    }

    static bool Matches(object? expected, object? actual) =>
        expected is IEnumerable sequence and not string && actual is IEnumerable other
            ? sequence.Cast<object?>().SequenceEqual(other.Cast<object?>()) : Equals(expected, actual);
}
