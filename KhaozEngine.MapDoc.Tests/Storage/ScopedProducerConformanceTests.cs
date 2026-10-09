using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class ScopedProducerConformanceTests
{
    [Fact]
    public void Acquire_CapableCustomProducerAndExplicitEmptyIncidentsComplete()
    {
        R2AcquisitionProbeSource probe = AcquisitionConformanceFixtures.OneSeed();
        MapSurfaceScope scope = AcquisitionConformanceFixtures.Scope();
        MapScopedSurfaces view = ScopeFixtures.Acquire(probe, scope);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        Assert.Equal((probe.SnapshotId, probe.RootSha256, scope.Frame), (view.ReadWitness.SnapshotId, view.Identity.RootSha256, view.Identity.Frame));
        Assert.Equal((true, true, true), (view.Witness.Complete, view.Identity.Complete, view.ReadWitness.Complete));
        Assert.Equal(new[] { "seed" }, view.Witness.SurfaceIds);
        Assert.Equal(new[] { AcquisitionConformanceFixtures.Seed }, view.Witness.Present.Select(p => p.Key));
        Assert.Equal(scope.Digest, view.Scope.Digest);
        Assert.Equal((0, 0, 0), (probe.LegacyFinds, probe.LegacyReads, probe.LegacySurfaceGets));
        Assert.Equal((1, 1), (probe.Session.Finds, probe.Session.Disposals));
        Assert.NotEmpty(probe.Session.SurfaceLookups);
        Assert.All(probe.Session.SurfaceLookups, id => Assert.Equal("seed", id));
        Assert.Equal(new[] { AcquisitionConformanceFixtures.Seed }, probe.Session.IncidentQueries);
        Assert.Empty(view.Witness.Records);
        Assert.Empty(view.Witness.Unavailable);
    }

    [Fact]
    public void Acquire_CopiesRequestBeforeOpeningTheSession()
    {
        R2AcquisitionProbeSource probe = AcquisitionConformanceFixtures.OneSeed();
        MapSurfacePatch seed = probe.Reads[AcquisitionConformanceFixtures.Seed].Patch!;
        seed.Records.Add(AcquisitionBoundFixtures.Space("room-original"));
        probe.SetRead(seed);
        MapSurfaceRole[] roles = { MapSurfaceRole.SupportFloor };
        string[] spaces = { "room-original" }, assets = { new('a', 64), new('b', 64) };
        MapSurfaceScope scope = AcquisitionConformanceFixtures.Scope() with { Roles = roles, SpaceIds = spaces };
        string requestDigest = scope.Digest;
        probe.DuringOpen = () => { roles[0] = MapSurfaceRole.Ceiling; spaces[0] = "room-mutated"; assets[0] = new('c', 64); };
        MapScopedSurfaces view = MapScopedSurfaces.Acquire(probe, scope, assets);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        Assert.Equal(new[] { MapSurfaceRole.SupportFloor }, probe.Session.Scope.Roles);
        Assert.Equal(new[] { "room-original" }, probe.Session.Scope.SpaceIds);
        Assert.Equal(new[] { MapSurfaceRole.SupportFloor }, view.Scope.Roles);
        Assert.Equal(new[] { "room-original" }, view.Scope.SpaceIds);
        Assert.Equal(new[] { new string('a', 64), new string('b', 64) }, view.Identity.AssetSha256);
        Assert.Equal((requestDigest, requestDigest, requestDigest), (view.Scope.Digest, view.Witness.ScopeDigest, view.Identity.ScopeDigest));
        Assert.NotSame(roles, view.Scope.Roles);
        Assert.NotSame(spaces, view.Scope.SpaceIds);
        Assert.NotSame(assets, view.Identity.AssetSha256);
        Assert.NotSame(roles, probe.Session.Scope.Roles);
        Assert.NotSame(spaces, probe.Session.Scope.SpaceIds);
    }

    [Fact]
    public void Acquire_LegacyOnlySourceRefusesBeforeCallbacks()
    {
        var source = new R2LegacyProbeSource();
        Assert.Contains("surface source does not support bounded scoped acquisition",
            Assert.Throws<MapDocumentException>(() => ScopeFixtures.Acquire(source, AcquisitionConformanceFixtures.Scope())).Message);
        Assert.Equal((0, 0, 0), (source.Finds, source.Reads, source.SurfaceGets));
    }

    [Fact]
    public void Acquire_UnknownIncidentsPublishIncompleteFacts()
    {
        R2AcquisitionProbeSource probe = AcquisitionConformanceFixtures.OneSeed();
        probe.Incidents.Add(AcquisitionConformanceFixtures.Seed, null);
        MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope());
        Assert.Equal(MapAcquireStatus.Incomplete, view.Status);
        Assert.Equal(new MapUnavailable("patch", AcquisitionConformanceFixtures.Seed, MapPatchStatus.Unloaded), Assert.Single(view.Witness.Unavailable));
        Assert.Contains("incident", view.Detail);
        Assert.Equal((false, false, false), (view.Witness.Complete, view.Identity.Complete, view.ReadWitness.Complete));
        Assert.Contains("incomplete", Assert.Throws<MapDocumentException>(view.Identity.RequireComplete).Message);
        Assert.Empty(view.Witness.KnownEmpty);
        Assert.Equal(MapPatchStatus.Present, view.Patch(AcquisitionConformanceFixtures.Seed).Status);
    }

    [Theory]
    [InlineData(MapPatchStatus.Present)]
    [InlineData(MapPatchStatus.Missing)]
    public void Acquire_IncidentOnlyAnchorIsIndependentOfEveryOtherPath(MapPatchStatus status)
    {
        MapDocument doc = AcquisitionConformanceFixtures.IsolatedIncident();
        var seed = new MapPatchKey("ground", 0, 0);
        var remote = new MapPatchKey("ground", 300, 0);
        var reference = new MapRecordRef("isolated-rim", remote);
        Assert.Empty(doc.Surfaces.Patches[seed].Records);
        Assert.Empty(doc.Surfaces.Patches[seed].CornerDependencies);
        Assert.Empty(doc.Surfaces.AllRecords().OfType<MapSpaceFootprint>());
        MapBoundaryChain chain = Assert.IsType<MapBoundaryChain>(Assert.Single(doc.Surfaces.Patches[remote].Records));
        Assert.Equal(MapChainKind.Authored, chain.Kind);
        Assert.Null(chain.SourcePatch);
        Assert.Equal(new[] { MapLatticeAddress.Corner(1, 0), MapLatticeAddress.Corner(1, 4) }, chain.Vertices.Select(v => v.Vertex.Address));
        Assert.All(chain.Vertices, v => { Assert.Equal("ground", v.Vertex.SurfaceId); Assert.Equal(1000, v.HeightUnits); });
        string dir = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            MapSurfaceScope scope = ScopeFixtures.Around(WorldFrame.Origin, 2, 2, 1.5f);
            using (IMapSurfaceAcquisitionSession session = MapStoredSurfaceSource.Open(dir).OpenAcquisition(scope))
            {
                MapPatchFindResult found = session.FindPatches();
                Assert.Equal(seed, Assert.Single(found.Patches).Key);
                Assert.DoesNotContain(found.Patches, p => p.Key == remote);
                Assert.DoesNotContain(found.KnownEmpty, r => Covers(r, remote));
                Assert.True(session.TryGetIncidentRecords(seed, out IReadOnlyList<MapRecordRef>? incidents));
                Assert.Equal(reference, Assert.Single(incidents!));
            }
            if (status == MapPatchStatus.Missing) SurfaceStorageFixtures.DeletePayload(dir, remote);
            MapScopedSurfaces view = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), scope);
            Assert.Equal(1, view.RecordReads);
            if (status == MapPatchStatus.Present)
            {
                Assert.Equal(MapAcquireStatus.Complete, view.Status);
                Assert.Equal(new[] { seed, remote }, view.Witness.Present.Select(p => p.Key));
                Assert.Equal(reference, Assert.Single(view.Witness.Records));
                Assert.True(view.TryRecord(reference, out MapTopologyRecord? acquired, out MapPatchStatus actual));
                Assert.Equal(MapPatchStatus.Present, actual);
                Assert.IsType<MapBoundaryChain>(acquired);
                Assert.Empty(view.Witness.Unavailable);
            }
            else
            {
                Assert.Equal(MapAcquireStatus.Incomplete, view.Status);
                Assert.Contains(new MapUnavailable("isolated-rim", remote, MapPatchStatus.Missing), view.Witness.Unavailable);
                Assert.False(view.TryRecord(reference, out _, out MapPatchStatus actual));
                Assert.Equal(MapPatchStatus.Missing, actual);
                Assert.DoesNotContain(view.Witness.KnownEmpty, r => Covers(r, remote));
                Assert.Equal((false, false, false), (view.Witness.Complete, view.Identity.Complete, view.ReadWitness.Complete));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Acquire_UndeclaredRequiredSurfaceRefusesAndDisposes()
    {
        R2AcquisitionProbeSource seed = AcquisitionConformanceFixtures.OneSeed();
        seed.Metadata.Clear();
        Assert.Throws<MapDocumentException>(() => ScopeFixtures.Acquire(seed, AcquisitionConformanceFixtures.Scope()));
        Assert.Contains("seed", seed.Session.SurfaceLookups);
        Assert.Empty(seed.Session.ExplicitReads);
        Assert.Equal(1, seed.Session.Disposals);

        R2AcquisitionProbeSource bound = AcquisitionConformanceFixtures.OneSeed();
        MapSurfacePatch patch = bound.Reads[AcquisitionConformanceFixtures.Seed].Patch!;
        AcquisitionBoundFixtures.Room(patch, "room", "room-cells", 0, "undeclared-low", "seed");
        bound.SetRead(patch);
        Assert.Throws<MapDocumentException>(() => ScopeFixtures.Acquire(bound, AcquisitionConformanceFixtures.Scope()));
        Assert.Contains("undeclared-low", bound.Session.SurfaceLookups);
        Assert.Empty(bound.Session.ExplicitReads);
        Assert.Equal(1, bound.Session.Disposals);
    }

    [Fact]
    public void Acquire_ReservationsDistinguishExplicitStatusesFromSentinels()
    {
        foreach (MapPatchStatus status in new[] { MapPatchStatus.Present, MapPatchStatus.KnownEmpty, MapPatchStatus.Missing, MapPatchStatus.Corrupt, MapPatchStatus.Unloaded })
        {
            R2AcquisitionProbeSource probe = AcquisitionConformanceFixtures.TwoAnchors();
            MapPatchKey a = AcquisitionConformanceFixtures.AnchorB, sentinel = AcquisitionConformanceFixtures.AnchorC;
            MapSurfacePatch seed = probe.Reads[AcquisitionConformanceFixtures.Seed].Patch!;
            seed.Records.Clear();
            foreach (string id in new[] { "fragment-one", "fragment-two" }) seed.Records.Add(new MapSpaceFootprint(id, new("b", a), seed.Key,
                new[] { 0 }, new(MapBoundKind.SupportFloor, "seed", null), new(MapBoundKind.OpenTop, null, null)));
            probe.SetRead(seed);
            probe.Reads[a] = status == MapPatchStatus.Present ? probe.Reads[a] : new(a, status, null, null, "directory unavailable", 0);
            probe.Sentinels.Add(new(sentinel, MapPatchStatus.Unloaded, null, null, "directory unavailable", 0));
            probe.CoveredEmpty.Add(new("anchor", new(30, 0, 33, 1)));
            MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope());
            Assert.Equal(MapAcquireStatus.Incomplete, view.Status);
            Assert.Equal(new[] { a }, probe.Session.ExplicitReads);
            Assert.Equal(new[] { a, AcquisitionConformanceFixtures.Seed }, probe.Session.ReservedAtDispose);
            Assert.Contains(new MapUnavailable("patch", sentinel, MapPatchStatus.Unloaded), view.Witness.Unavailable);
            if (status is MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded)
                Assert.Contains(new MapUnavailable("b", a, status), view.Witness.Unavailable);
            Assert.Equal(status is MapPatchStatus.Present or MapPatchStatus.KnownEmpty ? status : MapPatchStatus.Unloaded, view.Patch(a).Status);
            if (status is MapPatchStatus.Missing or MapPatchStatus.Corrupt or MapPatchStatus.Unloaded)
            {
                Assert.False(view.TryRecord(new("b", a), out _, out MapPatchStatus unavailable));
                Assert.Equal(status, unavailable);
            }
            Assert.DoesNotContain(sentinel, probe.Session.ReservedAtDispose);
            Assert.DoesNotContain(probe.Session.ReservedAtDispose, k => k.SurfaceId == "anchor" && k.SlotX >= 30 && k.SlotX < 33);
            Assert.All(probe.Session.ReservationSnapshots, snapshot =>
            {
                Assert.IsType<ReadOnlyCollection<MapPatchKey>>(snapshot);
                Assert.Equal(snapshot.OrderBy(k => k), snapshot);
                Assert.Equal(snapshot.Count, snapshot.Distinct().Count());
                Assert.Throws<NotSupportedException>(() => ((IList<MapPatchKey>)snapshot).Add(default));
            });
            Assert.Equal(new[] { AcquisitionConformanceFixtures.Seed }, probe.Session.ReservationSnapshots.First(s => s.Count == 1));
        }
    }

    [Fact]
    public void Acquire_AnchorKeysConsumeCandidateCapacityBeforeReads()
    {
        R2AcquisitionProbeSource probe = AcquisitionConformanceFixtures.TwoAnchors();
        MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope(new MapQueryLimits(MaxCandidatePatches: 2, MaxRecordReads: 64, MaxRecordDepth: 8)));
        Assert.Equal(MapAcquireStatus.CapacityExceeded, view.Status);
        AcquisitionConformanceFixtures.AssertNoFacts(view);
        Assert.Equal(new[] { AcquisitionConformanceFixtures.AnchorB }, probe.Session.ExplicitReads);
        Assert.DoesNotContain(AcquisitionConformanceFixtures.AnchorC, probe.Session.ExplicitReads);
        Assert.Equal(new[] { AcquisitionConformanceFixtures.AnchorB, AcquisitionConformanceFixtures.Seed }, probe.Session.ReservedAtDispose);
        Assert.Equal(1, probe.Session.Disposals);
    }

    [Fact]
    public void Acquire_RejectsIncoherentProducerFactsBeforePublication()
    {
        foreach (string fault in new[] { "returned-key", "snapshot", "scope", "semantic-digest", "collateral-reservation" })
        {
            R2AcquisitionProbeSource probe = AcquisitionConformanceFixtures.TwoAnchors();
            if (fault == "returned-key") probe.ReadFault = (_, read) => read with { Key = AcquisitionConformanceFixtures.AnchorC };
            if (fault == "snapshot") probe.FindFault = result => result with { SnapshotId = "other-snapshot" };
            if (fault == "scope") probe.FindFault = result => result with { Scope = result.Scope with { LocalMax = new Vector2(2, 2) } };
            if (fault == "semantic-digest")
            {
                Assert.NotEqual(new string('0', 64), MapSurfaceSemantics.PatchDigest(probe.Reads[AcquisitionConformanceFixtures.Seed].Patch!));
                probe.FindFault = result => result with { Patches = new[] { result.Patches[0] with { SemanticSha256 = new string('0', 64) } } };
            }
            if (fault == "collateral-reservation") probe.CollateralReservation = new("anchor", 99, 0);
            Assert.Throws<MapDocumentException>(() => ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope()));
            Assert.Equal(1, probe.Session.Disposals);
        }
    }

    [Theory]
    [InlineData("Complete")]
    [InlineData("Incomplete")]
    [InlineData("FindCapacity")]
    [InlineData("ReadCapacity")]
    [InlineData("ExactOverflow")]
    [InlineData("Unexpected")]
    public void Acquire_DisposesEveryTerminalSession(string terminal)
    {
        R2AcquisitionProbeSource probe = terminal == "ReadCapacity" ? AcquisitionConformanceFixtures.TwoAnchors() : AcquisitionConformanceFixtures.OneSeed();
        var unexpected = new InvalidOperationException("injected unexpected failure");
        if (terminal == "Incomplete") probe.Incidents.Add(AcquisitionConformanceFixtures.Seed, null);
        if (terminal == "FindCapacity") probe.FindStatus = MapFindStatus.CapacityExceeded;
        if (terminal == "ReadCapacity") probe.ReadException = new MapSurfaceCapacityException();
        if (terminal == "ExactOverflow") probe.FindException = new MapExactOverflowException();
        if (terminal == "Unexpected") probe.FindException = unexpected;
        if (terminal == "Unexpected") Assert.Same(unexpected, Assert.Throws<InvalidOperationException>(() => ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope())));
        else
        {
            MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope());
            MapAcquireStatus expected = terminal switch
            {
                "Complete" => MapAcquireStatus.Complete,
                "Incomplete" => MapAcquireStatus.Incomplete,
                "ExactOverflow" => MapAcquireStatus.NotRepresentable,
                _ => MapAcquireStatus.CapacityExceeded,
            };
            Assert.Equal(expected, view.Status);
            if (expected is MapAcquireStatus.CapacityExceeded or MapAcquireStatus.NotRepresentable) AcquisitionConformanceFixtures.AssertNoFacts(view);
            if (terminal == "FindCapacity") Assert.Equal(new[] { AcquisitionConformanceFixtures.Seed }, probe.Session.ReservedAtDispose);
            if (terminal == "ExactOverflow") Assert.Contains("overflow", view.Detail);
            var calls = (probe.Session.Finds, probe.Session.SurfaceLookups.Count, probe.Session.ExplicitReads.Count, probe.Session.IncidentQueries.Count,
                probe.LegacyFinds, probe.LegacyReads, probe.LegacySurfaceGets);
            _ = view.Patch(AcquisitionConformanceFixtures.Seed);
            _ = view.TryRecord(new("root", AcquisitionConformanceFixtures.Seed), out _, out _);
            _ = view.RecordsIn(AcquisitionConformanceFixtures.Seed).ToArray();
            _ = view.Surfaces.Count;
            _ = view.Identity.Digest;
            Assert.Equal(calls, (probe.Session.Finds, probe.Session.SurfaceLookups.Count, probe.Session.ExplicitReads.Count, probe.Session.IncidentQueries.Count,
                probe.LegacyFinds, probe.LegacyReads, probe.LegacySurfaceGets));
        }
        Assert.Equal(1, probe.Session.Disposals);
    }

    [Fact]
    public void Acquisition_DeepCopiesAllNestedRecordsAndNeededMetadata()
    {
        TopologyWorld world = TopologyRecordFixtures.WithEveryRecordKind();
        MapSurfacePatch floor = world.Patches.Single(p => p.Key.SurfaceId == "floor");
        string[] spaceTags = { "space-original" }, spanTags = { "span-original" };
        int index = floor.Records.FindIndex(r => r.Id == "a");
        floor.Records[index] = ((MapSpaceDoc)floor.Records[index]) with { DomainTags = spaceTags };
        world.Surfaces[0] = world.Surfaces[0] with { IndoorSpan = new("test-span", new("a", floor.Key), 0, 400, spanTags) };
        Assert.Empty(world.Validate());
        MapDocument doc = AcquisitionBoundFixtures.Document();
        doc.Surfaces.Refs.AddRange(world.Surfaces);
        foreach (MapSurfacePatch patch in world.Patches) AcquisitionBoundFixtures.Add(doc, patch);
        MapDocumentSurfaceSource captured = MapDocumentSurfaceSource.Capture(doc);
        var probe = new R2AcquisitionProbeSource(doc, world.Patches.Select(p => p.Key).ToArray());
        MapSurfaceScope scope = ScopeFixtures.Whole(doc);
        MapScopedSurfaces[] views = { ScopeFixtures.Acquire(captured, scope), ScopeFixtures.Acquire(probe, scope) };
        var expected = world.Patches.ToDictionary(p => p.Key, p => p.Clone());
        spaceTags[0] = "producer-space-mutated";
        spanTags[0] = "producer-span-mutated";
        foreach (MapSurfacePatch patch in world.Patches.Concat(probe.Session.ReturnedPatches)) AcquisitionConformanceFixtures.MutatePatch(patch);
        foreach (MapSurfaceRef metadata in probe.Session.ReturnedMetadata.Concat(probe.Metadata))
            if (metadata.IndoorSpan is { } span) AcquisitionConformanceFixtures.MutateFirst(span.DomainTags, "producer-span-mutated");
        foreach (MapScopedSurfaces view in views)
        {
            Assert.Equal(MapAcquireStatus.Complete, view.Status);
            string digest = view.Identity.Digest, root = view.Identity.RootSha256;
            KeyValuePair<MapPatchKey, string>[] witnessed = view.Witness.Present.ToArray();
            foreach (var row in expected)
            {
                AcquisitionConformanceFixtures.MutatePatch(view.Patch(row.Key).Patch!);
                foreach (MapTopologyRecord record in row.Value.Records)
                {
                    Assert.True(view.TryRecord(new(record.Id, row.Key), out MapTopologyRecord? copy, out MapPatchStatus status));
                    Assert.Equal(MapPatchStatus.Present, status);
                    AcquisitionConformanceFixtures.MutateRecord(copy!);
                }
                foreach (MapTopologyRecord copy in view.RecordsIn(row.Key)) AcquisitionConformanceFixtures.MutateRecord(copy);
                Assert.Equal(MapSurfaceSemantics.PatchDigest(row.Value), MapSurfaceSemantics.PatchDigest(view.Patch(row.Key).Patch!));
                foreach (MapTopologyRecord record in row.Value.Records)
                {
                    Assert.True(view.TryRecord(new(record.Id, row.Key), out MapTopologyRecord? copy, out _));
                    Assert.Equal(AcquisitionConformanceFixtures.RecordDigest(record), AcquisitionConformanceFixtures.RecordDigest(copy!));
                }
                Assert.Equal(row.Value.Records.OrderBy(r => r.Id, StringComparer.Ordinal).Select(AcquisitionConformanceFixtures.RecordDigest),
                    view.RecordsIn(row.Key).OrderBy(r => r.Id, StringComparer.Ordinal).Select(AcquisitionConformanceFixtures.RecordDigest));
            }
            MapIndoorSpan publishedSpan = view.Surfaces.Single(s => s.Id == "floor").IndoorSpan!;
            Assert.Equal(new[] { "span-original" }, publishedSpan.DomainTags);
            AcquisitionConformanceFixtures.MutateFirst(publishedSpan.DomainTags, "public-span-mutated");
            Assert.Equal(new[] { "span-original" }, view.Surfaces.Single(s => s.Id == "floor").IndoorSpan!.DomainTags);
            Assert.True(view.TryRecord(new("a", floor.Key), out MapTopologyRecord? space, out _));
            Assert.Equal(new[] { "space-original" }, Assert.IsType<MapSpaceDoc>(space).DomainTags);
            Assert.Equal(witnessed, view.Witness.Present);
            Assert.Equal((digest, digest, root), (view.Identity.Digest, view.ReadWitness.ScopedDigest, view.Identity.RootSha256));
        }
    }

    [Fact]
    public void Acquire_BoundReadsDoNotStartAnotherRecordExpansion()
    {
        MapDocument doc = AcquisitionBoundFixtures.AcquiredBounds();
        var low = new MapPatchKey("low", 0, 0);
        var third = new MapPatchKey("low", 10, 0);
        doc.Surfaces.Patches[low].Records.Add(AcquisitionBoundFixtures.Space("bound-only") with { Parent = new("third-room", third) });
        MapSurfacePatch remote = AcquisitionBoundFixtures.Patch(third, 0);
        remote.Records.Add(AcquisitionBoundFixtures.Space("third-room"));
        AcquisitionBoundFixtures.Add(doc, remote);
        var probe = new R2AcquisitionProbeSource(doc, new[] { new MapPatchKey("top", 0, 0) });
        MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionBoundFixtures.AcquiredScope with { Roles = new[] { MapSurfaceRole.Ceiling } });
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        Assert.Equal(new[] { low }, probe.Session.ExplicitReads);
        Assert.DoesNotContain(third, probe.Session.ExplicitReads);
        Assert.Contains("low", probe.Session.SurfaceLookups);
        Assert.Equal(0, view.RecordReads);
        Assert.Equal(MapPatchStatus.Present, view.Patch(low).Status);
        Assert.Equal(MapPatchStatus.Unloaded, view.Patch(third).Status);
    }

    [Fact]
    public void CompleteView_CapturesEveryResidentFactWithoutBoundEnumeration()
    {
        MapSurfaceSet set = AcquisitionBoundFixtures.AcquiredBounds().Surfaces;
        var refsOnly = new MapSurfaceSet();
        refsOnly.Refs.AddRange(set.Clone().Refs);
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(set);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        Assert.Equal((WorldFrame.Origin, Vector2.Zero, new Vector2(64, 64)), (view.Scope.Frame, view.Scope.LocalMin, view.Scope.LocalMax));
        Assert.Equal(new[] { new MapPatchKey("low", 0, 0), new("top", 0, 0) }, view.Witness.Present.Select(p => p.Key));
        Assert.Equal(new[] { "porch", "porch-cells" }, view.Witness.Records.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(MapScopedSurfaces.CompleteView(refsOnly).Identity.RootSha256, view.Identity.RootSha256);
        Assert.Equal((0, 0), (view.PagesRead, view.RecordReads));
        Assert.Equal((true, true, true), (view.Witness.Complete, view.Identity.Complete, view.ReadWitness.Complete));
        string digest = view.Identity.Digest;
        foreach (MapSurfacePatch patch in set.Patches.Values) AcquisitionConformanceFixtures.MutatePatch(patch);
        set.Refs.Clear();
        set.Patches.Clear();
        Assert.Equal(digest, view.Identity.Digest);
        Assert.Equal(300, view.Patch(new("top", 0, 0)).Patch!.Heights[0]);
        Assert.Equal(2, view.RecordsIn(new("top", 0, 0)).Count());
        Assert.Equal(2, view.Surfaces.Count);
        MapDocument bounded = AcquisitionBoundFixtures.MicroBound();
        int micro = bounded.Surfaces.Refs.FindIndex(s => s.Id == "micro");
        MapSurfaceRef lower = bounded.Surfaces.Refs[micro];
        bounded.Surfaces.Refs[micro] = lower with { Frame = lower.Frame with { CellUnitMetres = new(1, 1024) } };
        MapScopedSurfaces residentOnly = MapScopedSurfaces.CompleteView(bounded.Surfaces);
        Assert.Equal(MapAcquireStatus.Complete, residentOnly.Status);
        Assert.Equal(new[] { new MapPatchKey("micro", 0, 0), new("unit", 0, 0) }, residentOnly.Witness.Present.Select(p => p.Key));
        Assert.Equal(new[] { "slab", "slab-cells" }, residentOnly.Witness.Records.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal));
        MapScopedSurfaces empty = MapScopedSurfaces.CompleteView(new MapSurfaceSet());
        Assert.Equal(MapAcquireStatus.Complete, empty.Status);
        Assert.Equal((WorldFrame.Origin, Vector2.Zero, Vector2.Zero), (empty.Scope.Frame, empty.Scope.LocalMin, empty.Scope.LocalMax));
        Assert.Empty(empty.Witness.Present);
        Assert.Empty(empty.Witness.Records);
        Assert.Empty(empty.Surfaces);
        Assert.Equal((true, true, true), (empty.Witness.Complete, empty.Identity.Complete, empty.ReadWitness.Complete));
        _ = empty.Scope.Digest;
    }

    static bool Covers(MapCoveredRange range, MapPatchKey key) => range.SurfaceId == key.SurfaceId &&
        range.Slots.MinX <= key.SlotX && range.Slots.MaxXExclusive > key.SlotX &&
        range.Slots.MinZ <= key.SlotZ && range.Slots.MaxZExclusive > key.SlotZ;
}
