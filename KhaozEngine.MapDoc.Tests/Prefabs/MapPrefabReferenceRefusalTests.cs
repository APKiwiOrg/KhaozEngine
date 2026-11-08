using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabReferenceRefusalTests
{
    [Theory]
    [InlineData("slots", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("slot", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("path", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("target", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("count", MapDocumentRefusal.SchemaArrayLengthMismatch)]
    [InlineData("missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("kind", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("id", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("scope", MapDocumentRefusal.SchemaEnumUnsupported)]
    [InlineData("patch-world", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("record-scope", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("parent-world", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("parent-role", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("parent-space", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("parent-anchor", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("undeclared", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("anchor", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("patch", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("pose", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void Slot_admission_keeps_the_failed_rule_and_typed_subject(string fault, MapDocumentRefusal reason)
    {
        bool parent = fault.StartsWith("parent-", StringComparison.Ordinal);
        bool record = fault is "record-scope" or "anchor" or "parent-anchor";
        var patch = new MapPatchKey(parent ? "$parent" : "floor", 0, 0);
        string id = parent ? "$parent" : record ? "record" : "floor";
        string path = record ? "/value/space/id" : fault == "patch-world" ? "/patches/0/key/surfaceId" : "/value/surfaceId";
        var role = record ? MapPrefabOccurrenceRole.RecordId : parent ? MapPrefabOccurrenceRole.SpaceText
            : fault is "patch-world" or "patch" ? MapPrefabOccurrenceRole.PatchKey : MapPrefabOccurrenceRole.SurfaceText;
        var value = record ? new MapRecordRef(id, patch) : null;
        var kind = record || parent ? MapPrefabChildKind.Record : MapPrefabChildKind.Surface;
        MapPrefabOccurrence[] accepted = [new(path, kind, id, null, role, value, patch, parent)];
        if (record) accepted = [.. accepted, new("/value/space/anchor/surfaceId", MapPrefabChildKind.Surface, patch.SurfaceId,
            null, MapPrefabOccurrenceRole.RecordAnchor, value, patch, parent)];
        var occurrences = accepted.ToArray();
        var acceptedSlots = accepted.Select(o => new MapPrefabReferenceSlot(o.Path, new(MapPrefabReferenceScope.Local, o.Kind, o.Id))).ToArray();
        var slots = acceptedSlots.ToArray();
        MapTransform pose = new(Vector3.Zero, 0, 1);
        var targets = new Targets();
        switch (fault)
        {
            case "slots": slots = null!; break;
            case "slot": slots[0] = null!; break;
            case "path": slots[0] = slots[0] with { Path = null! }; break;
            case "target": slots[0] = slots[0] with { Target = null! }; break;
            case "duplicate": slots = [slots[0], slots[0]]; break;
            case "count": slots = []; break;
            case "missing": slots[0] = slots[0] with { Path = "/other" }; break;
            case "kind": slots[0] = slots[0] with { Target = slots[0].Target with { Kind = MapPrefabChildKind.Light } }; break;
            case "id": slots[0] = slots[0] with { Target = slots[0].Target with { Id = "other" } }; break;
            case "scope": slots[0] = slots[0] with { Target = slots[0].Target with { Scope = (MapPrefabReferenceScope)123 } }; break;
            case "patch-world" or "record-scope" or "parent-world":
                slots[0] = slots[0] with { Target = slots[0].Target with { Scope = MapPrefabReferenceScope.World } }; break;
            case "parent-role": occurrences[0] = occurrences[0] with { Role = MapPrefabOccurrenceRole.SurfaceText }; break;
            case "parent-space": occurrences[0] = occurrences[0] with { Space = false }; break;
            case "parent-anchor" or "anchor":
                var shifted = patch with { SlotX = 1 };
                occurrences = occurrences.Select(o => o with { Record = new(id, shifted), Patch = shifted }).ToArray(); break;
            case "undeclared":
                occurrences[0] = occurrences[0] with { Id = "missing" };
                slots[0] = slots[0] with { Target = slots[0].Target with { Id = "missing" } }; break;
            case "patch": occurrences[0] = occurrences[0] with { Patch = patch with { SlotX = 1 } }; break;
            case "pose": pose = pose with { Position = Vector3.UnitX }; break;
        }
        var beforeOccurrences = occurrences.ToArray();
        var beforeSlots = slots?.ToArray();
        var owner = new MapPrefabChildKey(MapPrefabChildKind.Record, "owner/not-parsed");
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabReferences.RequireSlots(slots!, occurrences, targets, pose, "developer prose", owner));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(owner, error.Child);
        Assert.Equal("$.references", error.FieldPath);
        if (fault == "count") Assert.Equal((0L, 1L), (error.ActualCount, error.ExpectedCount));
        if (fault == "anchor") Assert.Equal(patch, error.ActualAnchor);
        if (fault == "kind") Assert.Equal((MapPrefabChildKind.Light, MapPrefabChildKind.Surface), (error.ChildKind, error.ExpectedChildKind));
        if (fault == "scope") Assert.Equal((typeof(MapPrefabReferenceScope), 123L), (error.EnumType, error.ActualInteger));
        if (fault is not ("slots" or "slot" or "path" or "count")) Assert.Equal(path, error.ReferencePath);
        Assert.Equal(beforeOccurrences, occurrences);
        Assert.Equal(beforeSlots, slots);
        MapPrefabReferences.RequireSlots(acceptedSlots, accepted, targets, new(Vector3.Zero, 0, 1), "developer prose", owner);
    }

    [Fact]
    public void Unscoped_occurrence_accepts_an_explicit_scope()
    {
        var occurrence = new MapPrefabOccurrence("/value/surfaceId", MapPrefabChildKind.Surface, "floor", null,
            MapPrefabOccurrenceRole.SurfaceText, null, null, false);
        MapPrefabOccurrence[] input = [occurrence];
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabReferences.Slots(input));
        Assert.Equal(MapDocumentRefusal.PrefabReferenceScopeMissing, error.Reason);
        Assert.Equal((occurrence.Path, occurrence.Kind, occurrence.Id), (error.ReferencePath, error.ChildKind, error.EntityId));
        Assert.Equal(occurrence, Assert.Single(input));
        Assert.Equal(MapPrefabReferenceScope.World, Assert.Single(MapPrefabReferences.Slots(input, MapPrefabReferenceScope.World)).Target.Scope);
    }

    [Theory]
    [InlineData("binding", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("world-id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("slot", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("parent-text", MapDocumentRefusal.AuthoredValueRequired)]
    [InlineData("parent-record", MapDocumentRefusal.AuthoredValueRequired)]
    public void Remapping_keeps_missing_bindings_slots_and_parent_values_distinct(string fault, MapDocumentRefusal reason)
    {
        var asset = MapPrefabFile.Read(PrefabFixtures.AssetBytes("valid"), "hut");
        bool parentText = fault == "parent-text";
        string path = parentText ? "/value/supportBinding/spaceId" : "/value/supportBinding/surfaceId";
        var target = new MapPrefabReference(MapPrefabReferenceScope.Local, parentText ? MapPrefabChildKind.Record : MapPrefabChildKind.Surface,
            parentText ? "$parent" : "floor");
        MapPrefabChild child = fault == "parent-record" ? asset.Children.Single(c => c.Key.Key == "room")
            : new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "item"), new MapPlacement
            {
                Id = "item", Kind = "item", AssetId = "item", Y = 0, Scale = 1,
                SupportBinding = fault is "binding" or "world-id" ? null : new(parentText ? MapSupportBindingKind.Space : MapSupportBindingKind.Surface,
                    parentText ? null : target.Id, parentText ? target.Id : null, null, null, null),
            }) { References = fault is "binding" or "world-id" ? [] : [new(path, target)] };
        var bindings = asset.Children.ToDictionary(c => c.Key, c => "world/" + c.Key.Key);
        var supplied = bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (fault == "binding") supplied.Remove(child.Key);
        if (fault == "world-id") supplied[child.Key] = " ";
        var refused = fault == "slot" ? child with { References = [] } : child;
        byte[] before = MapPrefabJson.Bytes(refused);
        var beforeBindings = supplied.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabReferences.RemapChild(refused, supplied, null));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fault is "binding" or "world-id" ? "item" : fault == "slot" ? "floor" : "$parent", error.EntityId);
        if (fault is "binding" or "world-id") Assert.Equal(child.Key, error.Child);
        else Assert.Equal(fault == "parent-record" ? "/value/parent" : path, error.ReferencePath);
        if (fault == "parent-record") Assert.Equal(new MapPatchKey("$parent", 0, 0), error.Patch);
        if (fault == "world-id") Assert.Equal((" ", "$.bindings.worldId"), (error.WorldId, error.FieldPath));
        Assert.Equal(before, MapPrefabJson.Bytes(refused));
        Assert.Equal(beforeBindings, supplied.ToArray());
        var accepted = MapPrefabReferences.RemapChild(child, bindings, new("outside", new("outside", 0, 0)));
        Assert.Equal("world/" + child.Key.Key, accepted is MapPrefabPlacementChild p ? p.Value.Id : ((MapPrefabRecordChild)accepted).Value.Id);
    }

    [Fact]
    public void Remapped_support_patch_keys_cannot_merge_two_identities()
    {
        var surfaces = new MapSurfaceSet();
        foreach (string id in new[] { "left", "right" })
        {
            surfaces.Refs.Add(PrefabFixtures.Surface(id, MapSurfaceRole.SupportFloor));
            var patch = PrefabFixtures.Patch(id, 1, 1, 0);
            surfaces.Patches.Add(patch.Key, patch);
        }
        var resource = new MapSupportResource(1, surfaces, null, []);
        var before = surfaces.Patches.Keys.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabReferenceWalker.Resource(resource, new MergeSurfaceIds()));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal(new MapPatchKey("shared", 0, 0), error.Patch);
        Assert.Equal("shared", error.SurfaceId);
        Assert.Equal(before, surfaces.Patches.Keys);
        Assert.Equal(before, surfaces.Patches.Values.Select(p => p.Key));
        var accepted = MapPrefabReferenceWalker.Resource(resource, MapPrefabReferenceMap.Identity).Resource;
        Assert.Equal(2, accepted.Surfaces.Patches.Count);
        Assert.Equal(before.Order(), accepted.Surfaces.Patches.Keys.Order());
    }

    sealed class MergeSurfaceIds : MapPrefabReferenceMap
    {
        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space) =>
            ("shared", MapPrefabReferenceScope.Local);
    }

    sealed class Targets : IMapPrefabTargets
    {
        public bool Has(MapPrefabChildKind kind, string id) => (kind, id) is (MapPrefabChildKind.Surface, "floor") or (MapPrefabChildKind.Record, "record");
        public bool HasPatch(MapPatchKey key) => key == new MapPatchKey("floor", 0, 0);
        public MapPatchKey? Anchor(string recordId) => new MapPatchKey("floor", 0, 0);
        public MapTransform? Pose(MapPrefabChildKind kind, string id) => new(Vector3.Zero, 0, 1);
    }
}
