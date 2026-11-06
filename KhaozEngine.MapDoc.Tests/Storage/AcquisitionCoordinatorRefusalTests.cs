using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class AcquisitionCoordinatorRefusalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_read_must_return_the_requested_key(bool effective)
    {
        var probe = AcquisitionConformanceFixtures.TwoAnchors();
        probe.ReadFault = (_, read) => read with { Key = AcquisitionConformanceFixtures.AnchorC };
        var ex = Assert.Throws<MapDocumentException>(() => AcquisitionConformanceFixtures.Run(probe, effective));
        Assert.Equal(MapDocumentRefusal.AcquisitionContractMismatch, ex.Reason);
        Assert.Equal(AcquisitionConformanceFixtures.AnchorB, ex.ExpectedPatch);
        Assert.Equal(AcquisitionConformanceFixtures.AnchorC, ex.Patch);
        Assert.Equal("ReadPatch.Key", ex.FieldPath);
        Assert.Equal(AcquisitionConformanceFixtures.AnchorB, probe.Reads[AcquisitionConformanceFixtures.AnchorB].Patch!.Key);
        probe.ReadFault = null;
        Assert.Equal(MapAcquireStatus.Complete, AcquisitionConformanceFixtures.Run(probe, effective).Status);
    }

    [Theory]
    [InlineData(true, "missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData(true, "null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData(true, "identity", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData(false, "missing", MapDocumentRefusal.ReferenceMissing)]
    [InlineData(false, "null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData(false, "identity", MapDocumentRefusal.AuthoredValueMismatch)]
    public void Surface_lookup_identifies_the_actual_failed_branch(bool effective, string fault, MapDocumentRefusal reason)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        probe.SurfaceFault = _ => fault switch
        {
            "missing" => (false, null),
            "null" => (true, null),
            _ => (true, probe.Metadata[0] with { Id = "other" }),
        };
        var ex = Assert.Throws<MapDocumentException>(() => AcquisitionConformanceFixtures.Run(probe, effective));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("seed", ex.SurfaceId);
        Assert.Equal(fault == "identity" ? "Surface.Id" : "Surface", ex.FieldPath);
        if (fault == "identity") Assert.Equal("other", ex.ActualText);
        Assert.Equal("seed", Assert.Single(probe.Metadata).Id);
        probe.SurfaceFault = null;
        Assert.Equal(MapAcquireStatus.Complete, AcquisitionConformanceFixtures.Run(probe, effective).Status);
    }

    [Theory]
    [InlineData(true, "capacity", MapDocumentRefusal.WorkCapacityExceeded)]
    [InlineData(true, "duplicate", MapDocumentRefusal.OrderInvalid)]
    [InlineData(true, "order", MapDocumentRefusal.OrderInvalid)]
    [InlineData(false, "capacity", MapDocumentRefusal.WorkCapacityExceeded)]
    [InlineData(false, "duplicate", MapDocumentRefusal.OrderInvalid)]
    [InlineData(false, "order", MapDocumentRefusal.OrderInvalid)]
    public void Reservations_distinguish_capacity_from_canonical_order(bool effective, string fault, MapDocumentRefusal reason)
    {
        var probe = AcquisitionConformanceFixtures.TwoAnchors();
        probe.DiscoveryKeys = new[] { AcquisitionConformanceFixtures.Seed, AcquisitionConformanceFixtures.AnchorB };
        var scope = AcquisitionConformanceFixtures.Scope(new(MaxCandidatePatches: 3));
        probe.ReservationsFault = keys => fault switch
        {
            "capacity" => keys.Append(AcquisitionConformanceFixtures.AnchorC).Append(new MapPatchKey("anchor", 30, 0)).ToArray(),
            "duplicate" => new[] { keys[0], keys[0] },
            _ => keys.Reverse().ToArray(),
        };
        var ex = Assert.Throws<MapDocumentException>(() => AcquisitionConformanceFixtures.Run(probe, effective, scope));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("ReservedPatchKeys", ex.FieldPath);
        Assert.Equal(fault == "capacity" ? 4L : 2L, ex.ActualCount);
        if (fault == "capacity")
        {
            Assert.Equal("MaxCandidatePatches", ex.Budget);
            Assert.Equal(3L, ex.Limit);
        }
        Assert.Equal(3, probe.Reads.Count);
        probe.ReservationsFault = null;
        // The three real keys exactly fill the cap after expansion, including the remaining anchor.
        Assert.Equal(MapAcquireStatus.Complete, AcquisitionConformanceFixtures.Run(probe, effective, scope).Status);
    }

    [Theory]
    [InlineData(true, "snapshot")]
    [InlineData(true, "root")]
    [InlineData(true, "reservations")]
    [InlineData(false, "snapshot")]
    [InlineData(false, "root")]
    [InlineData(false, "reservations")]
    public void Reconciliation_preserves_the_pin_that_changed(bool effective, string fault)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        probe.IncidentFault = _ =>
        {
            if (fault == "snapshot") probe.SessionSnapshotId = "changed";
            else if (fault == "root") probe.SessionRootSha256 = new string('f', 64);
            else probe.ReservationsFault = _ => new[] { new MapPatchKey("seed", 1, 0) };
            return (true, Array.Empty<MapRecordRef>());
        };
        var ex = Assert.Throws<MapDocumentException>(() => AcquisitionConformanceFixtures.Run(probe, effective));
        Assert.Equal(MapDocumentRefusal.AcquisitionSnapshotChanged, ex.Reason);
        Assert.Equal(fault == "snapshot" ? "SnapshotId" : fault == "root" ? "RootSha256" : "ReservedPatchKeys", ex.FieldPath);
        if (fault == "snapshot")
        {
            Assert.Equal(probe.SnapshotId, ex.ExpectedSnapshot);
            Assert.Equal("changed", ex.ActualSnapshot);
        }
        if (fault == "root")
        {
            Assert.Equal(probe.RootSha256, ex.ExpectedHash);
            Assert.Equal(new string('f', 64), ex.ActualHash);
        }
        Assert.Equal(AcquisitionConformanceFixtures.Seed, Assert.Single(probe.Reads).Key);
        probe.IncidentFault = null;
        probe.SessionSnapshotId = probe.SessionRootSha256 = null;
        probe.ReservationsFault = null;
        Assert.Equal(MapAcquireStatus.Complete, AcquisitionConformanceFixtures.Run(probe, effective).Status);
    }

    [Theory]
    [InlineData(true, "negative", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(true, "backward", MapDocumentRefusal.AcquisitionContractMismatch)]
    [InlineData(true, "delta", MapDocumentRefusal.AcquisitionContractMismatch)]
    [InlineData(false, "negative", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(false, "backward", MapDocumentRefusal.AcquisitionContractMismatch)]
    [InlineData(false, "delta", MapDocumentRefusal.AcquisitionContractMismatch)]
    public void Page_observations_distinguish_negative_backward_and_inconsistent_counts(bool effective, string fault, MapDocumentRefusal reason)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        if (fault == "backward") probe.PageCount = 1;
        probe.FindFault = found =>
        {
            probe.PageCount = fault == "delta" ? 1 : 0;
            return found with { PagesRead = fault == "negative" ? -1 : 0 };
        };
        var ex = Assert.Throws<MapDocumentException>(() => AcquisitionConformanceFixtures.Run(probe, effective));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("PagesRead", ex.FieldPath);
        if (fault == "negative")
        {
            Assert.Equal(-1L, ex.ActualInteger);
            Assert.Equal(0L, ex.MinimumInteger);
        }
        else
        {
            Assert.Equal(0L, ex.ActualCount);
            Assert.Equal(fault == "backward" ? -1L : 1L, ex.ExpectedCount);
        }
        Assert.Single(probe.Reads);
        probe.FindFault = null;
        probe.PageCount = 0;
        Assert.Equal(MapAcquireStatus.Complete, AcquisitionConformanceFixtures.Run(probe, effective).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resident_publication_distinguishes_key_and_surface_ownership(bool mismatchedKey)
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        var set = AcquisitionConformanceFixtures.Context(probe).Surfaces;
        MapSurfacePatch patch = set.Patches[AcquisitionConformanceFixtures.Seed];
        if (mismatchedKey) patch.Key = new("seed", 1, 0);
        else set.Refs.Clear();
        var ex = Assert.Throws<MapDocumentException>(() => MapScopedSurfaces.CompleteView(set));
        Assert.Equal(mismatchedKey ? MapDocumentRefusal.AuthoredValueMismatch : MapDocumentRefusal.ReferenceMissing, ex.Reason);
        Assert.Equal(mismatchedKey ? "Patches.Key" : "Patches.SurfaceId", ex.FieldPath);
        Assert.Equal(mismatchedKey ? patch.Key : AcquisitionConformanceFixtures.Seed, ex.Patch);
        if (mismatchedKey) Assert.Equal(AcquisitionConformanceFixtures.Seed, ex.ExpectedPatch);
        else Assert.Equal("seed", ex.SurfaceId);
        Assert.Equal(AcquisitionConformanceFixtures.Seed, Assert.Single(set.Patches).Key);
        if (mismatchedKey) patch.Key = AcquisitionConformanceFixtures.Seed;
        else set.Refs.AddRange(probe.Metadata);
        Assert.Equal(MapAcquireStatus.Complete, MapScopedSurfaces.CompleteView(set).Status);
    }

    [Fact]
    public void Effective_cell_collapse_retains_its_patch_and_local_cell()
    {
        var probe = AcquisitionConformanceFixtures.OneSeed();
        var context = AcquisitionConformanceFixtures.Context(probe);
        var transforms = new Dictionary<string, MapEffectiveTransform>();
        // A unit cell scaled by 2^-22 freezes both endpoints to zero on the 2^-20 lattice.
        transforms.Add("Surface:seed", new(0, 0, 0, 0, 1.0 / (4 * MapResourceTransform.Quantum)));
        context = new(context.Surfaces, transforms, context.Digest);
        var ex = Assert.Throws<MapDocumentException>(() => new MapEffectiveSurfaceSource(context));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, ex.Reason);
        Assert.Equal(AcquisitionConformanceFixtures.Seed, ex.Patch);
        Assert.Equal("seed", ex.SurfaceId);
        Assert.Equal((0, 0), (ex.CellX, ex.CellZ));
        Assert.Single(context.Surfaces.Patches);
        // One quantum is the smallest nonzero frozen edge, with exact normal binary64 arithmetic.
        transforms["Surface:seed"] = new(0, 0, 0, 0, 1.0 / MapResourceTransform.Quantum);
        var control = new MapEffectiveContext(context.Surfaces, transforms, context.Digest);
        Assert.Equal(MapPatchStatus.Present, new MapEffectiveSurfaceSource(control).ReadPatch(AcquisitionConformanceFixtures.Seed).Status);
    }
}
