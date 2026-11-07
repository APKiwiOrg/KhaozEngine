using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class AcquiredFactRefusalTests
{
    [Theory]
    [InlineData("surface", MapDocumentRefusal.SchemaValueRequired, "surface")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "Id")]
    [InlineData("frame", MapDocumentRefusal.SchemaValueRequired, "Frame")]
    [InlineData("role", MapDocumentRefusal.SchemaEnumUnsupported, "Role")]
    [InlineData("presence", MapDocumentRefusal.SchemaEnumUnsupported, "PresencePolicy")]
    public void Surface_metadata_identifies_the_first_failed_branch(string fault, MapDocumentRefusal reason, string field)
    {
        var facts = new MapAcquiredFacts();
        MapSurfaceRef valid = Surface();
        MapSurfaceRef refused = fault switch
        {
            "surface" => null!,
            "id" => valid with { Id = " " },
            "frame" => valid with { Frame = null! },
            "role" => valid with { Role = (MapSurfaceRole)255 },
            _ => valid with { PresencePolicy = (MapPresencePolicy)255 },
        };
        var ex = Assert.Throws<MapDocumentException>(() => facts.AddSurface(refused));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(field, ex.FieldPath);
        Assert.Equal(refused?.Id, ex.SurfaceId);
        if (reason == MapDocumentRefusal.SchemaEnumUnsupported)
        {
            Assert.Equal(fault == "role" ? typeof(MapSurfaceRole) : typeof(MapPresencePolicy), ex.EnumType);
            Assert.Equal(255L, ex.ActualInteger);
        }
        Assert.Empty(facts.Surfaces);
        facts.AddSurface(valid);
        Assert.Equal(valid, Assert.Single(facts.Surfaces));
    }

    [Theory]
    [InlineData("status", MapDocumentRefusal.SchemaEnumUnsupported)]
    [InlineData("pages", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    public void Read_admission_distinguishes_status_page_count_and_duplicate_key(string fault, MapDocumentRefusal reason)
    {
        var facts = new MapAcquiredFacts();
        MapPatchRead valid = Read(SurfacePatchFixtures.Row(1));
        if (fault == "duplicate") facts.AddRead(valid);
        MapPatchRead refused = fault switch
        {
            "status" => valid with { Status = (MapPatchStatus)255 },
            "pages" => valid with { PagesRead = -1 },
            _ => valid,
        };
        var ex = Assert.Throws<MapDocumentException>(() => facts.AddRead(refused));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(valid.Key, ex.Patch);
        Assert.Equal(refused.Status, ex.PatchStatus);
        if (fault == "status")
        {
            Assert.Equal("Status", ex.FieldPath);
            Assert.Equal(typeof(MapPatchStatus), ex.EnumType);
            Assert.Equal(255L, ex.ActualInteger);
        }
        if (fault == "pages")
        {
            Assert.Equal("PagesRead", ex.FieldPath);
            Assert.Equal(-1L, ex.ActualInteger);
            Assert.Equal(0L, ex.MinimumInteger);
        }
        Assert.Equal(fault == "duplicate" ? 1 : 0, facts.Reads.Count());
        if (fault == "duplicate")
        {
            MapSurfacePatch second = valid.Patch!.Clone();
            second.Key = new("r", 1, 0);
            valid = Read(second);
        }
        facts.AddRead(valid);
        Assert.True(facts.HasRead(valid.Key));
        Assert.Equal(0, facts.CopyPatch(valid.Key).PagesRead);
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("key", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("digest", MapDocumentRefusal.DigestMismatch)]
    [InlineData("unavailable", MapDocumentRefusal.AcquisitionContractMismatch)]
    public void Read_payload_preserves_the_refused_contract_and_truthful_identifiers(string fault, MapDocumentRefusal reason)
    {
        var facts = new MapAcquiredFacts();
        MapPatchRead valid = Read(SurfacePatchFixtures.Row(1));
        MapSurfacePatch other = valid.Patch!.Clone();
        other.Key = new("r", 1, 0);
        MapPatchRead refused = fault switch
        {
            "missing" => valid with { Patch = null },
            "key" => valid with { Patch = other },
            "digest" => valid with { SemanticSha256 = new string('0', 64) },
            _ => valid with { Status = MapPatchStatus.Unloaded },
        };
        var ex = Assert.Throws<MapDocumentException>(() => facts.AddRead(refused));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(refused.Status, ex.PatchStatus);
        Assert.Equal(fault == "key" ? other.Key : valid.Key, ex.Patch);
        if (fault == "key") Assert.Equal(valid.Key, ex.ExpectedPatch);
        if (fault == "digest")
        {
            Assert.Equal(refused.SemanticSha256, ex.ExpectedHash);
            Assert.Equal(valid.SemanticSha256, ex.ActualHash);
        }
        Assert.Empty(facts.Reads);
        facts.AddRead(valid);
        Assert.Equal(valid.Key, Assert.Single(facts.Reads).Key);
    }

    [Fact]
    public void Record_identity_is_unique_across_acquired_patches()
    {
        var facts = new MapAcquiredFacts();
        MapSurfacePatch first = SurfacePatchFixtures.Row(1);
        first.Records.Add(new MapHorizontalOpening("opening", first.Key, new[] { 0 }));
        facts.AddRead(Read(first));
        MapSurfacePatch second = SurfacePatchFixtures.Row(1);
        second.Key = new("r", 1, 0);
        second.Records.Add(new MapHorizontalOpening("opening", second.Key, new[] { 0 }));
        var ex = Assert.Throws<MapDocumentException>(() => facts.AddRead(Read(second)));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, ex.Reason);
        Assert.Equal("opening", ex.RecordId);
        Assert.Equal(second.Key, ex.Patch);
        Assert.Equal(first.Key, Assert.Single(facts.Reads).Key);
        Assert.Equal(new MapRecordRef("opening", first.Key), Assert.Single(facts.Records));
        second.Records[0] = second.Records[0] with { Id = "other" };
        facts.AddRead(Read(second));
        Assert.Equal(2, facts.Reads.Count());
        Assert.Equal(new[] { "opening", "other" }, facts.Records.Select(r => r.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_range_requires_a_range_and_surface_identity(bool missing)
    {
        var facts = new MapAcquiredFacts();
        var valid = new MapCoveredRange("r", new(0, 0, 1, 1));
        MapCoveredRange refused = missing ? null! : valid with { SurfaceId = " " };
        var ex = Assert.Throws<MapDocumentException>(() => facts.AddEmpty(refused));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, ex.Reason);
        Assert.Equal(missing ? "range" : "SurfaceId", ex.FieldPath);
        Assert.Equal(refused?.SurfaceId, ex.SurfaceId);
        Assert.Empty(facts.KnownEmpty);
        facts.AddEmpty(valid);
        Assert.Equal(valid, Assert.Single(facts.KnownEmpty));
    }

    [Fact]
    public void Acquired_key_requires_its_surface_identity()
    {
        var key = new MapPatchKey(" ", 0, 0);
        var ex = Assert.Throws<MapDocumentException>(() => MapAcquiredFacts.ValidateKey(key));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, ex.Reason);
        Assert.Equal("SurfaceId", ex.FieldPath);
        Assert.Equal(key, ex.Patch);
        MapAcquiredFacts.ValidateKey(key with { SurfaceId = "r" });
    }

    [Fact]
    public void Bound_preflight_requires_the_bound_surface_identity()
    {
        MapSurfaceRef surface = Surface();
        var key = new MapPatchKey("r", 0, 0);
        var valid = new MapSpaceFootprint("footprint", new("room", key), key, new[] { 0 },
            new(MapBoundKind.SupportFloor, "r", null), new(MapBoundKind.OpenTop, null, null));
        var refused = valid with { Lower = valid.Lower with { SurfaceId = " " } };
        var scope = ScopeFixtures.Around(WorldFrame.Origin, 0.5f, -0.5f, 0.25f);
        var ex = Assert.Throws<MapDocumentException>(() => MapBoundAcquisition.Collect(scope, new[] { refused },
            Array.Empty<MapPatchKey>(), _ => surface, null));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, ex.Reason);
        Assert.Equal("Lower.SurfaceId", ex.FieldPath);
        Assert.Equal("footprint", ex.RecordId);
        Assert.NotEmpty(MapBoundAcquisition.Collect(scope, new[] { valid }, Array.Empty<MapPatchKey>(), _ => surface, null));
    }

    [Fact]
    public void Composed_source_requires_scoped_acquisition_from_its_native_producer()
    {
        var document = AcquisitionBoundFixtures.AcquiredBounds();
        var context = new MapEffectiveContext(document.Surfaces, new Dictionary<string, MapEffectiveTransform>(), new string('a', 64));
        var native = MapDocumentSurfaceSource.Capture(document);
        var legacy = new R2UnscopedSurfaceSource(native);
        var source = new MapComposedSurfaceSource(context, legacy);
        var ex = Assert.Throws<MapDocumentException>(() => source.OpenAcquisition(AcquisitionBoundFixtures.AcquiredScope));
        Assert.Equal(MapDocumentRefusal.AcquisitionUnsupported, ex.Reason);
        Assert.Equal(typeof(R2UnscopedSurfaceSource), ex.SourceType);
        using var session = new MapComposedSurfaceSource(context, native).OpenAcquisition(AcquisitionBoundFixtures.AcquiredScope);
        Assert.Equal(MapFindStatus.Complete, session.FindPatches().Status);
    }

    static MapSurfaceRef Surface() => new("r", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
        MapPresencePolicy.Native, null, null, new string('a', 64));
    static MapPatchRead Read(MapSurfacePatch patch) => new(patch.Key, MapPatchStatus.Present, patch,
        MapSurfaceSemantics.PatchDigest(patch), null, 0);

}
