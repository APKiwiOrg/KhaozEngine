using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class EffectiveAcquisitionRefusalTests
{
    [Theory]
    [InlineData("snapshot", MapDocumentRefusal.AcquisitionSnapshotChanged, "SnapshotId")]
    [InlineData("scope-null", MapDocumentRefusal.SchemaValueRequired, "Scope")]
    [InlineData("scope", MapDocumentRefusal.AcquisitionContractMismatch, "Scope.Digest")]
    [InlineData("status", MapDocumentRefusal.SchemaEnumUnsupported, "Status")]
    [InlineData("payload-status", MapDocumentRefusal.AcquisitionContractMismatch, "Patches.Status")]
    [InlineData("unreserved", MapDocumentRefusal.AcquisitionContractMismatch, "Patches.Key")]
    [InlineData("unavailable-status", MapDocumentRefusal.SchemaEnumUnsupported, "Unavailable.Status")]
    [InlineData("unavailable-payload", MapDocumentRefusal.AcquisitionContractMismatch, "Unavailable.Patch")]
    [InlineData("missing-fact", MapDocumentRefusal.AcquisitionContractMismatch, "ReservedPatchKeys")]
    public void Effective_discovery_identifies_each_producer_contract_branch(string fault, MapDocumentRefusal reason, string field)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        MapEffectiveContext context = Context(probe);
        probe.FindFault = found => fault switch
        {
            "snapshot" => found with { SnapshotId = "changed" },
            "scope-null" => found with { Scope = null! },
            "scope" => found with { Scope = found.Scope with { LocalMax = new(2, 2) } },
            "status" => found with { Status = (MapFindStatus)255 },
            "payload-status" => found with { Patches = new[] { found.Patches[0] with { Status = MapPatchStatus.KnownEmpty, Patch = null, SemanticSha256 = null } } },
            "unreserved" => found with { Patches = found.Patches.Append(OtherRead(probe)).ToArray() },
            "unavailable-status" => found with { Unavailable = new[] { new MapPatchRead(new("seed", 1, 0), (MapPatchStatus)255, null, null, null, 0) } },
            "unavailable-payload" => found with { Unavailable = new[] { OtherRead(probe) with { Status = MapPatchStatus.Unloaded } } },
            _ => found with { Patches = Array.Empty<MapPatchRead>() },
        };
        var ex = Assert.Throws<MapDocumentException>(() => Run(probe, context));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(field, ex.FieldPath);
        if (fault == "snapshot")
        {
            Assert.Equal(probe.SnapshotId, ex.ExpectedSnapshot);
            Assert.Equal("changed", ex.ActualSnapshot);
        }
        if (fault == "scope")
        {
            Assert.Equal(AcquisitionConformanceFixtures.Scope().Digest, ex.ExpectedHash);
            Assert.Equal((AcquisitionConformanceFixtures.Scope() with { LocalMax = new(2, 2) }).Digest, ex.ActualHash);
        }
        if (fault is "status" or "unavailable-status")
        {
            Assert.Equal(fault == "status" ? typeof(MapFindStatus) : typeof(MapPatchStatus), ex.EnumType);
            Assert.Equal(255L, ex.ActualInteger);
        }
        if (fault is "payload-status" or "missing-fact") Assert.Equal(AcquisitionConformanceFixtures.Seed, ex.Patch);
        if (fault is "unreserved" or "unavailable-status" or "unavailable-payload") Assert.Equal(new MapPatchKey("seed", 1, 0), ex.Patch);
        Assert.Equal(AcquisitionConformanceFixtures.Seed, Assert.Single(context.Surfaces.Patches).Key);
        probe.FindFault = null;
        Assert.Equal(MapAcquireStatus.Complete, Run(probe, context).Status);
    }

    [Theory]
    [InlineData("unknown", MapDocumentRefusal.AcquisitionContractMismatch)]
    [InlineData("known-null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("reference-null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("reference-id", MapDocumentRefusal.SchemaValueRequired)]
    public void Effective_incidence_distinguishes_unknown_known_and_required_references(string fault, MapDocumentRefusal reason)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        MapEffectiveContext context = Context(probe);
        probe.IncidentFault = _ => fault switch
        {
            "unknown" => (false, Array.Empty<MapRecordRef>()),
            "known-null" => (true, null),
            "reference-null" => (true, new MapRecordRef[] { null! }),
            _ => (true, new[] { new MapRecordRef(" ", AcquisitionConformanceFixtures.Seed) }),
        };
        var ex = Assert.Throws<MapDocumentException>(() => Run(probe, context));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(fault is "unknown" or "known-null" ? "IncidentRecords" : fault == "reference-null" ? "reference" : "Id", ex.FieldPath);
        if (fault != "reference-null") Assert.Equal(AcquisitionConformanceFixtures.Seed, ex.Patch);
        if (fault == "reference-id") Assert.Equal(" ", ex.RecordId);
        Assert.Single(context.Surfaces.Patches);
        probe.IncidentFault = null;
        Assert.Equal(MapAcquireStatus.Complete, Run(probe, context).Status);
    }

    [Theory]
    [InlineData(true, "blank")]
    [InlineData(true, "snapshot")]
    [InlineData(true, "root")]
    [InlineData(false, "blank")]
    [InlineData(false, "snapshot")]
    [InlineData(false, "root")]
    public void Session_admission_preserves_the_expected_pin(bool effective, string fault)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        if (fault != "root") probe.SessionSnapshotId = fault == "blank" ? " " : "changed";
        else probe.SessionRootSha256 = new string('f', 64);
        var ex = Assert.Throws<MapDocumentException>(() => AcquisitionConformanceFixtures.Run(probe, effective));
        Assert.Equal(fault == "blank" ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.AcquisitionContractMismatch, ex.Reason);
        Assert.Equal(fault == "root" ? "RootSha256" : "SnapshotId", ex.FieldPath);
        if (fault == "snapshot")
        {
            Assert.Equal(probe.SnapshotId, ex.ExpectedSnapshot);
            Assert.Equal("changed", ex.ActualSnapshot);
        }
        if (fault == "root")
        {
            Assert.Equal(probe.RootSha256, ex.ExpectedHash);
            Assert.Equal(probe.SessionRootSha256, ex.ActualHash);
        }
        probe.SessionSnapshotId = probe.SessionRootSha256 = null;
        Assert.Equal(MapAcquireStatus.Complete, AcquisitionConformanceFixtures.Run(probe, effective).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composed_session_must_match_its_captured_native_pin(bool root)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        MapEffectiveContext context = Context(probe);
        var source = new MapComposedSurfaceSource(context, probe);
        if (root) probe.SessionRootSha256 = new string('f', 64);
        else probe.SessionSnapshotId = "changed";
        var ex = Assert.Throws<MapDocumentException>(() => source.OpenAcquisition(AcquisitionConformanceFixtures.Scope()));
        Assert.Equal(MapDocumentRefusal.AcquisitionContractMismatch, ex.Reason);
        Assert.Equal(root ? "RootSha256" : "SnapshotId", ex.FieldPath);
        if (root)
        {
            Assert.Equal(probe.RootSha256, ex.ExpectedHash);
            Assert.Equal(probe.SessionRootSha256, ex.ActualHash);
        }
        else
        {
            Assert.Equal(probe.SnapshotId, ex.ExpectedSnapshot);
            Assert.Equal("changed", ex.ActualSnapshot);
        }
        probe.SessionSnapshotId = probe.SessionRootSha256 = null;
        using var session = source.OpenAcquisition(AcquisitionConformanceFixtures.Scope());
        Assert.Equal(MapFindStatus.Complete, session.FindPatches().Status);
    }

    [Fact]
    public void Composed_read_identifies_an_undeclared_owner()
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        using var session = new MapComposedSurfaceSource(Context(probe), probe).OpenAcquisition(AcquisitionConformanceFixtures.Scope());
        Assert.Equal(MapFindStatus.Complete, session.FindPatches().Status);
        var key = new MapPatchKey("missing", 0, 0);
        var ex = Assert.Throws<MapDocumentException>(() => session.ReadPatch(key));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, ex.Reason);
        Assert.Equal(key, ex.Patch);
        Assert.Equal("missing", ex.SurfaceId);
        Assert.Equal(MapPatchStatus.Present, session.ReadPatch(AcquisitionConformanceFixtures.Seed).Status);
    }

    static MapScopedAcquisition.Result Run(R2AcquisitionProbeSource probe, MapEffectiveContext context) =>
        MapEffectiveAcquisition.Run(probe, AcquisitionConformanceFixtures.Scope(), null, context);
    static MapEffectiveContext Context(R2AcquisitionProbeSource probe)
    {
        var set = new MapSurfaceSet();
        set.Refs.AddRange(probe.Metadata);
        foreach (var read in probe.Reads.Values) if (read.Patch is { } patch) set.Patches.Add(read.Key, patch);
        return new(set, new Dictionary<string, MapEffectiveTransform>(), probe.RootSha256);
    }
    static MapPatchRead OtherRead(R2AcquisitionProbeSource probe)
    {
        MapSurfacePatch patch = probe.Reads[AcquisitionConformanceFixtures.Seed].Patch!.Clone();
        patch.Key = new("seed", 1, 0);
        return new(patch.Key, MapPatchStatus.Present, patch, MapSurfaceSemantics.PatchDigest(patch), null, 0);
    }
}
