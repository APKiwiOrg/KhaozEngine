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
using KhaozEngine.MapDoc.Surfaces;
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
    public void Resource_ids_are_an_owned_read_only_snapshot()
    {
        string[] source = ["a", "b"];
        var refusal = new MapDocumentException(MapDocumentRefusal.AssetDependencyCycle, "cycle") { ResourceIds = source };
        var finding = new MapDocumentFinding(MapDocumentRefusal.AssetDependencyCycle, "cycle") { ResourceIds = source };
        source[0] = "changed";
        Assert.Equal(new[] { "a", "b" }, refusal.ResourceIds);
        if (refusal.ResourceIds is IList<string> list)
            Assert.Throws<NotSupportedException>(() => list[0] = "changed");
        Assert.Equal(new[] { "a", "b" }, refusal.ResourceIds);
        Assert.Equal(new[] { "a", "b" }, finding.ResourceIds);
        if (finding.ResourceIds is IList<string> findingList)
            Assert.Throws<NotSupportedException>(() => findingList[0] = "changed");
        Assert.Equal(new[] { "a", "b" }, finding.ResourceIds);
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
        }
        Assert.NotNull(MapDocumentFinding.FromException(new(MapDocumentRefusal.IdentityDuplicate, "accepted typed projection")));
    }

    static object Sample(PropertyInfo property, int index)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (property.Name == nameof(MapDocumentException.OwnerId)) return "terrain-owner";
        if (property.Name == nameof(MapDocumentException.Operation)) return "Prepare";
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

    static bool Matches(object? expected, object? actual) =>
        expected is IEnumerable sequence and not string && actual is IEnumerable other
            ? sequence.Cast<object?>().SequenceEqual(other.Cast<object?>()) : Equals(expected, actual);
}
