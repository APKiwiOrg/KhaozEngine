using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabRemainingRefusalTests
{
    [Theory]
    [InlineData("nested", MapDocumentRefusal.PrefabPreviousOverrideNestedRemove)]
    [InlineData("previous", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("replace", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("add", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("kind", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("tags", MapDocumentRefusal.SchemaValueRequired)]
    public void Override_rules_keep_actual_and_required_child_keys(string fault, MapDocumentRefusal reason)
    {
        var fixture = PrefabFixtures.Create(fault == "kind" ? "basic" : "roomless");
        var original = Assert.Single(fixture.Document.Prefabs!.Instances);
        var asset = fixture.Assets.GetPrefab(original.ResourceId);
        var placement = asset.Children.OfType<MapPrefabPlacementChild>().First();
        var target = fault == "kind" ? asset.Children.OfType<MapPrefabStoreyChild>().First().Key
            : fault == "add" ? new MapPrefabChildKey(MapPrefabChildKind.Storey, "added") : placement.Key;
        var other = new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "other"), "Other");
        var pose = new MapTransform(Vector3.Zero, 0, 1);
        MapPrefabOverride refused = fault switch
        {
            "nested" => new MapPrefabRemoveOverride(target, new MapPrefabRemoveOverride(target)),
            "previous" => new MapPrefabRemoveOverride(target, new MapPrefabTransformOverride(other.Key, pose)),
            "replace" => new MapPrefabReplaceOverride(target, other),
            "add" => new MapPrefabAddOverride(target, other),
            "kind" => new MapPrefabPlacementOverride(target, null, null, null),
            _ => new MapPrefabPlacementOverride(target, null, null, [null!]),
        };
        var bindings = original.Bindings.Entries.Select(b => fault is "nested" or "previous" && b.Child == target ? b with { Removed = true } : b).ToArray();
        if (fault == "add") bindings = [.. bindings, new(target, "a/added", null, false)];
        var invalid = original with { Bindings = new(bindings), Overrides = [refused] };
        fixture.Document.Prefabs = new(1, 1, [invalid], []);
        long mark = fixture.Document.NumericIdHighWaterMark;
        var finding = MapPrefabDocumentRefusalTests.Refused(fixture.Document, fixture.Assets, reason);
        if (fault is "previous" or "replace" or "add")
        {
            Assert.Equal(other.Key, finding.Child);
            Assert.Equal(target, finding.ExpectedChild);
        }
        else Assert.Equal(target, finding.Child);
        if (fault == "kind") Assert.Equal(MapPrefabChildKind.Placement, finding.ExpectedChildKind);
        Assert.Same(invalid, Assert.Single(fixture.Document.Prefabs.Instances));
        Assert.Equal(mark, fixture.Document.NumericIdHighWaterMark);
        MapPrefabOverride accepted = fault switch
        {
            "nested" or "previous" => new MapPrefabRemoveOverride(target, new MapPrefabTransformOverride(target, pose)),
            "replace" => new MapPrefabReplaceOverride(target, placement),
            "add" => new MapPrefabAddOverride(target, other with { Key = target }),
            "kind" => new MapPrefabTransformOverride(target, pose),
            _ => new MapPrefabPlacementOverride(target, null, null, []),
        };
        fixture.Document.Prefabs = new(1, 1, [invalid with { Overrides = [accepted] }], []);
        Assert.Empty(MapPrefabValidation.Validate(fixture.Document, fixture.Assets));
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(true, MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    public void Binding_distinguishes_the_numeric_domain_from_child_kind(bool wrongKind, MapDocumentRefusal reason)
    {
        var document = MapPrefabDocumentRefusalTests.Empty();
        var key = new MapPrefabChildKey(wrongKind ? MapPrefabChildKind.Storey : MapPrefabChildKind.Placement, "child");
        var binding = new MapPrefabBinding(key, "world", wrongKind ? 1 : 0, false);
        var instance = new MapPrefabInstance("instance", "resource", new string('a', 64), new(Vector3.Zero, 0, 1), null, new([binding]), []) { Removed = true };
        document.Prefabs = new(1, 1, [instance], []);
        var finding = MapPrefabDocumentRefusalTests.Refused(document, null, reason);
        Assert.Equal(key, finding.Child);
        Assert.Equal("world", finding.WorldId);
        Assert.Equal(binding.NumericId, finding.NumericId);
        if (wrongKind) Assert.Equal(MapPrefabChildKind.Placement, finding.ExpectedChildKind);
        else Assert.Equal(1, finding.MinimumInteger);
        Assert.Same(instance, Assert.Single(document.Prefabs.Instances));
        document.Prefabs = new(1, 1, [instance with { Bindings = new([binding with { NumericId = wrongKind ? null : 1 }]) }], []);
        Assert.Empty(MapPrefabValidation.Validate(document, null));
    }

    [Theory]
    [InlineData("scope", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("kind", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    public void Effective_reference_admission_distinguishes_scope_kind_and_existence(string fault, MapDocumentRefusal reason)
    {
        var owner = new MapResolvedPlacement("roof", "roof", "asset", null, new(Vector3.Zero, 0, 1), []);
        var valid = PrefabFixtures.World(MapPrefabChildKind.Placement, "roof");
        var refused = fault switch
        {
            "scope" => valid with { Scope = MapPrefabReferenceScope.Local },
            "kind" => valid with { Kind = MapPrefabChildKind.Record },
            _ => valid with { Id = "missing" },
        };
        var intent = new MapBuildingIntent(1, [], [], [], []) { Roofs = [refused] };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireEffectiveAnnotations(intent, [owner], new()));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(refused.Id, error.EntityId);
        Assert.Equal(refused.Scope, error.ReferenceScope);
        Assert.Equal(MapPrefabReferenceScope.World, error.ExpectedReferenceScope);
        Assert.Equal(refused.Kind, error.ChildKind);
        Assert.Equal(MapPrefabChildKind.Placement, error.ExpectedChildKind);
        Assert.Equal("roof placement", error.SubjectKind);
        Assert.Same(refused, Assert.Single(intent.Roofs));
        MapPrefabValidation.RequireEffectiveAnnotations(new(1, [], [], [], []) { Roofs = [valid] }, [owner], new());
    }

    [Theory]
    [InlineData("group-room", MapPrefabChildKind.Record, "roof room")]
    [InlineData("roof", MapPrefabChildKind.Placement, "inventoried roof")]
    [InlineData("link-room", MapPrefabChildKind.Record, "roof room")]
    [InlineData("group", MapPrefabChildKind.RoofGroup, "roof group")]
    [InlineData("light", MapPrefabChildKind.Placement, "light owner")]
    public void Effective_reference_roles_use_their_own_target_namespace(string role, MapPrefabChildKind kind, string subject)
    {
        var roof = PrefabFixtures.World(MapPrefabChildKind.Placement, "roof");
        var room = PrefabFixtures.World(MapPrefabChildKind.Record, "room");
        var group = PrefabFixtures.World(MapPrefabChildKind.RoofGroup, "group");
        var patch = PrefabFixtures.Patch("floor", 1, 1, 0);
        patch.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Room, null, null, [], [], [], []));
        var surfaces = new MapSurfaceSet(); surfaces.Patches.Add(patch.Key, patch);
        var owner = new MapResolvedPlacement("roof", "roof", "asset", null, new(Vector3.Zero, 0, 1), []);
        MapBuildingIntent Intent(bool invalid) => new(1,
            [new("link", invalid && role == "roof" ? roof with { Id = "missing" } : roof,
                [invalid && role == "link-room" ? room with { Id = "missing" } : room],
                [invalid && role == "group" ? group with { Id = "missing" } : group], 0)],
            [new("group", [invalid && role == "group-room" ? room with { Id = "missing" } : room])],
            [new(1, "lamp", invalid && role == "light" ? roof with { Id = "missing" } : roof, Vector3.Zero, Vector3.One, 0, 1, false)], [])
        { Roofs = [roof] };
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabValidation.RequireEffectiveAnnotations(Intent(true), [owner], surfaces));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("missing", error.EntityId);
        Assert.Equal(kind, error.ExpectedChildKind);
        Assert.Equal(subject, error.SubjectKind);
        Assert.Single(patch.Records);
        MapPrefabValidation.RequireEffectiveAnnotations(Intent(false), [owner], surfaces);
    }
}
