using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public class MapSupportResourceTests
{
    [Theory]
    [InlineData("object", MapDocumentRefusal.SchemaObjectRequired, "$")]
    [InlineData("missing", MapDocumentRefusal.SchemaMemberMissing, "$.references")]
    [InlineData("unknown", MapDocumentRefusal.SchemaMemberUnknown, "$.extra")]
    [InlineData("duplicate", MapDocumentRefusal.SchemaMemberDuplicate, "$.references")]
    [InlineData("patch", MapDocumentRefusal.IdentityDuplicate, "$.patches")]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.payloadVersion")]
    [InlineData("version-type", MapDocumentRefusal.SchemaValueRequired, "$.payloadVersion")]
    [InlineData("json", MapDocumentRefusal.SchemaMalformed, null)]
    public void SupportPayload_ReportsItsOwnAdmissionRule(string fault, MapDocumentRefusal reason, string? field)
    {
        byte[] valid = MapSupportResourceFile.Write(PrefabFixtures.DeckSupport());
        var root = JsonNode.Parse(valid)!.AsObject();
        switch (fault)
        {
            case "missing": root.Remove("references"); break;
            case "unknown": root["extra"] = true; break;
            case "version": root["payloadVersion"] = 2; break;
            case "version-type": root["payloadVersion"] = "future"; break;
            case "patch": root["patches"]!.AsArray().Add(root["patches"]![0]!.DeepClone()); break;
        }
        string json = fault == "object" ? "[]" : fault == "json" ? "{" : root.ToJsonString();
        if (fault == "duplicate") json = json[..^1] + ",\"references\":[]}";
        var error = Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Read(Encoding.UTF8.GetBytes(json), "support"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("support", error.ResourceId);
        if (field is not null) Assert.Equal(field, error.FieldPath);
        if (fault == "version")
        {
            Assert.Equal(2, error.ActualVersion);
            Assert.Equal(new[] { 1 }, error.SupportedVersions);
        }
        if (fault == "version-type") Assert.Null(error.ActualVersion);
        if (fault == "patch") Assert.Equal(PrefabFixtures.DeckSupport().Surfaces.Patches.Single().Key, error.Patch);
        Assert.Equal(valid, MapSupportResourceFile.Write(MapSupportResourceFile.Read(valid, "support")));
    }

    [Theory]
    [InlineData("surfaces")]
    [InlineData("references")]
    public void SupportPayload_RejectsNullCollections(string field)
    {
        var valid = PrefabFixtures.DeckSupport();
        var invalid = field == "surfaces" ? valid with { Surfaces = null! } : valid with { References = null! };
        var error = Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Write(invalid));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("$." + field, error.FieldPath);
        Assert.NotEmpty(MapSupportResourceFile.Write(valid));
    }

    [Fact]
    public void SupportPayload_RejectsRecordsRepeatedAcrossPatches()
    {
        var resource = PrefabFixtures.DeckSupport();
        var first = resource.Surfaces.Patches.Single().Value;
        var second = first.Clone();
        second.Key = first.Key with { SlotX = 1 };
        resource.Surfaces.Patches.Add(second.Key, second);
        var error = Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Write(resource));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, error.Reason);
        Assert.Equal("top-edge", error.RecordId);
        Assert.Equal("support resource record", error.SubjectKind);
        Assert.Equal(second.Key, error.Patch);
        resource.Surfaces.Patches.Remove(second.Key);
        Assert.NotEmpty(MapSupportResourceFile.Write(resource));
    }

    [Fact]
    public void DryVariant_PreservesGeometryIdsAndDefaultWater()
    {
        var fixture = PrefabFixtures.Create("all-resources");
        var before = PrefabFixtures.Effective(fixture);
        var dry = PrefabFixtures.Effective(PrefabFixtures.DryAllResources());
        Assert.Single(before.Water!.Bodies);
        Assert.Single(before.Water.Media);
        Assert.Null(dry.Water);
        Assert.Equal(before.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)),
            dry.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)));
        Assert.Equal(before.Surfaces.Refs, dry.Surfaces.Refs);
        Assert.Equal(before.Surfaces.Patches.Select(p => (p.Key, MapSurfaceSemantics.PatchDigest(p.Value))),
            dry.Surfaces.Patches.Select(p => (p.Key, MapSurfaceSemantics.PatchDigest(p.Value))));
        Assert.Single(PrefabFixtures.Effective(fixture).Water!.Bodies);
    }

    [Theory]
    [InlineData(MapSurfaceRole.Ceiling, MapPresencePolicy.Native, MapDocumentRefusal.SurfaceSupportRoleInvalid)]
    [InlineData(MapSurfaceRole.PaintOverride, MapPresencePolicy.Native, MapDocumentRefusal.SurfaceSupportRoleInvalid)]
    [InlineData(MapSurfaceRole.SupportFloor, MapPresencePolicy.LegacyTileWorld, MapDocumentRefusal.SurfaceSupportRoleInvalid)]
    [InlineData((MapSurfaceRole)255, MapPresencePolicy.Native, MapDocumentRefusal.SchemaEnumUnsupported)]
    [InlineData(MapSurfaceRole.SupportFloor, (MapPresencePolicy)255, MapDocumentRefusal.SchemaEnumUnsupported)]
    public void ResourceRejectsNonSupportRoles(MapSurfaceRole role, MapPresencePolicy presence, MapDocumentRefusal reason)
    {
        var resource = new MapSupportResource(1, PrefabFixtures.FloorResource(role), null,
            PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "floor")));
        resource.Surfaces.Refs[0] = resource.Surfaces.Refs[0] with { PresencePolicy = presence };
        var error = Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Write(resource));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("floor", error.SurfaceId);
        Assert.Equal(role == MapSurfaceRole.SupportFloor ? "$.surfaces[0].presencePolicy" : "$.surfaces[0].role", error.FieldPath);
        resource.Surfaces.Refs[0] = resource.Surfaces.Refs[0] with { Role = MapSurfaceRole.SupportFloor, PresencePolicy = MapPresencePolicy.Native };
        Assert.NotEmpty(MapSupportResourceFile.Write(resource));
    }

    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("empty", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    public void Support_surface_identity_keeps_the_actual_rule(string fault, MapDocumentRefusal reason)
    {
        var resource = PrefabFixtures.DeckSupport();
        var originals = resource.Surfaces.Refs.ToArray();
        if (fault == "null") resource.Surfaces.Refs[0] = null!;
        else if (fault == "empty") resource.Surfaces.Refs[0] = originals[0] with { Id = " " };
        else resource.Surfaces.Refs.Add(originals[0]);
        var error = Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Require(resource, "support"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("support", error.ResourceId);
        Assert.Equal(fault == "duplicate" ? originals.Length : 0, error.Index);
        if (fault == "duplicate") Assert.Equal(originals[0].Id, error.SurfaceId);
        resource.Surfaces.Refs.Clear();
        resource.Surfaces.Refs.AddRange(originals);
        MapSupportResourceFile.Require(resource, "support");
    }

    [Fact]
    public void Support_byte_budget_accepts_its_exact_limit_without_allocating_a_payload()
    {
        long limit = MapPrefabJson.Limits.MaxPayloadBytes;
        var error = Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.RequirePayloadSize(limit + 1, "support"));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("support", error.ResourceId);
        Assert.Equal("MaxPayloadBytes", error.Budget);
        Assert.Equal(limit + 1, error.ActualByteCount);
        Assert.Equal(limit, error.Limit);
        MapSupportResourceFile.RequirePayloadSize(limit, "support");
    }

    [Fact]
    public void ResourceRejectsInvalidMetreFrame()
    {
        var set = PrefabFixtures.FloorResource();
        set.Refs[0] = set.Refs[0] with { Frame = set.Refs[0].Frame with { RowDirection = (MapRowDirection)99 } };
        var resource = new MapSupportResource(1, set, null,
            PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "floor")));
        Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Write(resource));
    }

    [Fact]
    public void ImplicitFloorBindingsAndRoomAnchorsPersistWithoutAllocation()
    {
        var f = PrefabFixtures.Create("support-resource");
        long highWater = f.Document.NumericIdHighWaterMark;
        var reloaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        var resources = MapPrefabResolver.Expand(reloaded, f.Assets);
        Assert.Contains(resources.Surfaces.Refs, s => s.Id == PrefabFixtures.SupportFloorId);
        var room = resources.Surfaces.AllRecords().Single(r => r.Id == "a/room");
        Assert.Contains(resources.Surfaces.Patches[new(PrefabFixtures.SupportFloorId, 0, 0)].Records, r => r == room);
        Assert.Equal(highWater, reloaded.NumericIdHighWaterMark);
        Assert.Null(PrefabFixtures.LiveBindings(reloaded).Single(b => b.WorldId == PrefabFixtures.SupportFloorId).NumericId);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, -450)]
    public void ImplicitSupportMissingY_ResolvesInMapDocBeforePhysics(bool far, float expectedY)
    {
        var f = PrefabFixtures.SupportAt(far);
        long highWater = f.Document.NumericIdHighWaterMark;
        var resolution = MapResolverV2.Resolve(f.Document, f.Assets, MapDocumentSurfaceSource.Capture(f.Document), PrefabFixtures.Options());
        Assert.Contains(resolution.Document.EffectiveResources!.Surfaces.Refs, s => s.Id == PrefabFixtures.SupportFloorId);
        var support = Assert.Single(resolution.Supports);
        Assert.Equal(MapSupportStatus.Supported, support.Support.Status);
        Assert.NotNull(support.Support.Face);
        Assert.Equal(expectedY, support.Support.WorldY);
        Assert.Equal(expectedY, resolution.Document.Placements.Single(p => p.PlacementId == "low-item").Transform.Position.Y);
        Assert.Equal(highWater, f.Document.NumericIdHighWaterMark);
    }
}
