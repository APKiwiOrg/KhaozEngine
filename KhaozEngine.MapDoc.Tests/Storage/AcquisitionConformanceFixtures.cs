using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

internal static class AcquisitionConformanceFixtures
{
    internal static readonly MapPatchKey Seed = new("seed", 0, 0), AnchorB = new("anchor", 10, 0), AnchorC = new("anchor", 20, 0);
    internal static MapSurfaceScope Scope(MapQueryLimits? limits = null) => ScopeFixtures.Around(WorldFrame.Origin, 0.5f, 0.5f, 0.25f, limits);
    internal static R2AcquisitionProbeSource OneSeed()
    {
        MapDocument doc = AcquisitionBoundFixtures.Document();
        AcquisitionBoundFixtures.Surface(doc, Seed.SurfaceId, MapSurfaceRole.SupportFloor);
        AcquisitionBoundFixtures.Add(doc, AcquisitionBoundFixtures.Patch(Seed, 1000));
        return new(doc, new[] { Seed });
    }
    internal static R2AcquisitionProbeSource TwoAnchors()
    {
        MapDocument doc = AcquisitionBoundFixtures.Document();
        AcquisitionBoundFixtures.Surface(doc, "seed", MapSurfaceRole.SupportFloor);
        AcquisitionBoundFixtures.Surface(doc, "anchor", MapSurfaceRole.SupportFloor);
        MapSurfacePatch seed = AcquisitionBoundFixtures.Patch(Seed, 1000);
        seed.Records.Add(AcquisitionBoundFixtures.Space("root") with { Parent = new("b", AnchorB), AliasOf = new("c", AnchorC) });
        AcquisitionBoundFixtures.Add(doc, seed);
        foreach (var row in new[] { (AnchorB, "b"), (AnchorC, "c") })
        {
            MapSurfacePatch patch = AcquisitionBoundFixtures.Patch(row.Item1, 1000);
            patch.Records.Add(AcquisitionBoundFixtures.Space(row.Item2) with { Parent = row.Item1 == AnchorC ? new("b", AnchorB) : null });
            AcquisitionBoundFixtures.Add(doc, patch);
        }
        return new(doc, new[] { Seed });
    }
    internal static MapDocument IsolatedIncident()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(1);
        var remote = new MapPatchKey("ground", 300, 0);
        MapSurfacePatch patch = SurfaceStorageFixtures.FlatPatch(remote, 1000);
        patch.Records.Add(new MapBoundaryChain("isolated-rim", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("ground", MapLatticeAddress.Corner(1, 0)), 1000),
            new MapChainVertex(new("ground", MapLatticeAddress.Corner(1, 4)), 1000),
        }));
        AcquisitionBoundFixtures.Add(doc, patch);
        return doc;
    }
    internal static (R2AcquisitionProbeSource Probe, MapSurfaceScope Scope) CapThreeBounds()
    {
        MapDocument doc = AcquisitionBoundFixtures.Document();
        AcquisitionBoundFixtures.Surface(doc, "seed", MapSurfaceRole.SupportFloor);
        AcquisitionBoundFixtures.Surface(doc, "top", MapSurfaceRole.Ceiling);
        AcquisitionBoundFixtures.Surface(doc, "low", MapSurfaceRole.SupportFloor, new(3, 128));
        MapSurfacePatch top = AcquisitionBoundFixtures.Patch(new("top", 0, 0), 300, minX: 1);
        AcquisitionBoundFixtures.Room(top, "cap-room", "cap-cells", 1, "low", "top");
        AcquisitionBoundFixtures.Add(doc, top);
        var probe = new R2AcquisitionProbeSource(doc, new[] { Seed, top.Key });
        probe.Reads.Add(Seed, new(Seed, MapPatchStatus.KnownEmpty, null, null, null, 0));
        return (probe, ScopeFixtures.Around(WorldFrame.Origin, 1.5f, 0.5f, 0.25f, new MapQueryLimits(MaxCandidatePatches: 3)));
    }
    internal static (R2AcquisitionProbeSource Probe, MapSurfaceScope Scope) GrossOverflowBounds()
    {
        MapDocument doc = AcquisitionBoundFixtures.Document();
        AcquisitionBoundFixtures.Surface(doc, "top", MapSurfaceRole.Ceiling, new(int.MaxValue, 1));
        AcquisitionBoundFixtures.Surface(doc, "low", MapSurfaceRole.SupportFloor, new(1, int.MaxValue));
        MapSurfacePatch top = AcquisitionBoundFixtures.Patch(new("top", 0, 0), 300);
        AcquisitionBoundFixtures.Room(top, "huge-room", "huge-cells", 0, "low", "top");
        AcquisitionBoundFixtures.Add(doc, top);
        return (new(doc, new[] { top.Key }), Scope() with { Roles = new[] { MapSurfaceRole.Ceiling } });
    }
    internal static void AssertNoFacts(MapScopedSurfaces view)
    {
        Assert.Empty(view.Witness.SurfaceIds);
        Assert.Empty(view.Witness.Present);
        Assert.Empty(view.Witness.KnownEmpty);
        Assert.Empty(view.Witness.Records);
        Assert.Empty(view.Witness.Unavailable);
        Assert.Empty(view.Identity.Patches);
        Assert.Empty(view.Identity.Records);
        Assert.Empty(view.Identity.KnownEmpty);
        Assert.Empty(view.Surfaces);
        Assert.Equal((false, false, false), (view.Witness.Complete, view.Identity.Complete, view.ReadWitness.Complete));
        foreach (MapPatchKey key in new[] { Seed, AnchorB, AnchorC, new("top", 0, 0), new("low", 0, 0),
            new("ground", 0, 0), new("ground", 40, 0), new("roof", 40, 0), new("roof", 41, 0) })
        {
            Assert.Equal(MapPatchStatus.Unloaded, view.Patch(key).Status);
            Assert.Null(view.Patch(key).Patch);
            Assert.Empty(view.RecordsIn(key));
            foreach (string id in new[] { "root", "b", "c", "cap-room", "cap-cells", "huge-room", "huge-cells", "outside", "door-top" })
            {
                Assert.False(view.TryRecord(new(id, key), out MapTopologyRecord? record, out MapPatchStatus status));
                Assert.Null(record);
                Assert.Equal(MapPatchStatus.Unloaded, status);
            }
        }
    }

    internal static string RecordDigest(MapTopologyRecord record)
    {
        MapSurfacePatch patch = AcquisitionBoundFixtures.Patch(Seed, 1000);
        patch.Records.Add(record);
        return MapSurfaceSemantics.PatchDigest(patch);
    }
    internal static void MutateFirst<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.NotEmpty(values);
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        if (values is T[] array) array[0] = replacement;
        else if (list.IsReadOnly) Assert.Throws<NotSupportedException>(() => list[0] = replacement);
        else list[0] = replacement;
    }
    internal static void MutatePatch(MapSurfacePatch patch)
    {
        patch.Heights[0] = 7;
        patch.Cells[0] = default;
        patch.Presence[0] = 0;
        foreach (MapTopologyRecord record in patch.Records) MutateRecord(record);
        patch.Records.Clear();
    }
    internal static void MutateRecord(MapTopologyRecord record)
    {
        var wrong = new MapRecordRef("mutated", new("mutated", 99, 99));
        switch (record)
        {
            case MapSurfaceSeam seam:
                MutateFirst(seam.Pairs, (default(MapLatticeVertex), default(MapLatticeVertex)));
                break;
            case MapBoundaryChain chain:
                MutateFirst(chain.Vertices, chain.Vertices[0] with { HeightUnits = 7 });
                break;
            case MapCavePortal portal: MutateFirst(portal.Interval, default(MapLatticeVertex)); break;
            case MapHorizontalOpening opening: MutateFirst(opening.SlotCells, 7); break;
            case MapSpaceFootprint footprint: MutateFirst(footprint.SlotCells, 7); break;
            case MapVerticalLink link:
                MutateFirst(link.Openings, wrong);
                MutateFirst(link.Portals, wrong);
                MutateFirst(link.GeometryOwners, wrong);
                break;
            case MapSpaceDoc space:
                if (space.DomainTags.Count != 0) MutateFirst(space.DomainTags, "mutated");
                if (space.Walls.Count != 0) MutateFirst(space.Walls, new MapBoundaryRef(wrong, MapSide.Back));
                if (space.Portals.Count != 0) MutateFirst(space.Portals, new MapBoundaryRef(wrong, MapSide.Back));
                if (space.Links.Count != 0) MutateFirst(space.Links, wrong);
                break;
        }
    }
}

/// <summary>A finite trusted probe for coordinator observations, never backend budget admission.</summary>
internal sealed class R2AcquisitionProbeSource : IMapSurfaceAcquisitionSource
{
    internal R2AcquisitionProbeSource(MapDocument doc, IReadOnlyList<MapPatchKey> discoveryKeys)
    {
        MapSurfaceSet copy = doc.Surfaces.Clone();
        Metadata = copy.Refs;
        foreach (MapSurfacePatch patch in copy.Patches.Values) SetRead(patch);
        DiscoveryKeys = discoveryKeys.ToArray();
        RootSha256 = MapSurfaceSemantics.RootDigest(doc);
    }
    public string SnapshotId => "r2-probe:fixed";
    public string RootSha256 { get; }
    internal List<MapSurfaceRef> Metadata { get; }
    internal Dictionary<MapPatchKey, MapPatchRead> Reads { get; } = new();
    internal IReadOnlyList<MapPatchKey> DiscoveryKeys { get; set; }
    internal Dictionary<MapPatchKey, IReadOnlyList<MapRecordRef>?> Incidents { get; } = new();
    internal List<MapCoveredRange> CoveredEmpty { get; } = new();
    internal List<MapPatchRead> Sentinels { get; } = new();
    internal Action? DuringOpen { get; set; }
    internal Func<MapPatchFindResult, MapPatchFindResult>? FindFault { get; set; }
    internal Func<MapPatchKey, MapPatchRead, MapPatchRead>? ReadFault { get; set; }
    internal Exception? FindException { get; set; }
    internal Exception? ReadException { get; set; }
    internal MapFindStatus? FindStatus { get; set; }
    internal MapPatchKey? CollateralReservation { get; set; }
    internal R2AcquisitionProbeSession Session { get; private set; } = null!;
    internal int LegacySurfaceGets { get; private set; }
    internal int LegacyFinds { get; private set; }
    internal int LegacyReads { get; private set; }
    public IReadOnlyList<MapSurfaceRef> Surfaces { get { LegacySurfaceGets++; return Metadata; } }
    public MapPatchRead ReadPatch(MapPatchKey key) { LegacyReads++; throw new InvalidOperationException("legacy read called"); }
    public MapPatchFindResult FindPatches(MapSurfaceScope scope) { LegacyFinds++; throw new InvalidOperationException("legacy find called"); }
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope)
    {
        DuringOpen?.Invoke();
        Session = new(this, scope);
        return Session;
    }
    internal void SetRead(MapSurfacePatch patch) => Reads[patch.Key] =
        new(patch.Key, MapPatchStatus.Present, patch, MapSurfaceSemantics.PatchDigest(patch), null, 0);
}

internal sealed class R2AcquisitionProbeSession : IMapSurfaceAcquisitionSession
{
    readonly R2AcquisitionProbeSource _source;
    readonly SortedSet<MapPatchKey> _reserved = new();
    readonly HashSet<MapPatchKey> _served = new();
    bool _found, _disposed;
    internal R2AcquisitionProbeSession(R2AcquisitionProbeSource source, MapSurfaceScope scope)
    {
        _source = source;
        Scope = scope with { Roles = Array.AsReadOnly(scope.Roles.ToArray()), SpaceIds = scope.SpaceIds is null ? null : Array.AsReadOnly(scope.SpaceIds.ToArray()) };
    }
    internal MapSurfaceScope Scope { get; }
    public string SnapshotId => _source.SnapshotId;
    public string RootSha256 => _source.RootSha256;
    public int PagesRead => 0;
    internal int Finds { get; private set; }
    internal int Disposals { get; private set; }
    internal List<string> SurfaceLookups { get; } = new();
    internal List<MapPatchKey> ExplicitReads { get; } = new();
    internal List<MapPatchKey> IncidentQueries { get; } = new();
    internal List<MapSurfacePatch> ReturnedPatches { get; } = new();
    internal List<MapSurfaceRef> ReturnedMetadata { get; } = new();
    internal List<IReadOnlyList<MapPatchKey>> ReservationSnapshots { get; } = new();
    internal IReadOnlyList<MapPatchKey> ReservedAtDispose { get; private set; } = Array.Empty<MapPatchKey>();
    public IReadOnlyList<MapPatchKey> ReservedPatchKeys
    {
        get
        {
            Alive();
            IReadOnlyList<MapPatchKey> copy = Array.AsReadOnly(_reserved.ToArray());
            ReservationSnapshots.Add(copy);
            return copy;
        }
    }
    public bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface)
    {
        Alive();
        SurfaceLookups.Add(surfaceId);
        MapSurfaceRef? found = _source.Metadata.SingleOrDefault(s => s.Id == surfaceId);
        surface = found is null ? null : found with
        {
            IndoorSpan = found.IndoorSpan is { } span ? span with { DomainTags = span.DomainTags.ToArray() } : null,
        };
        if (surface is not null) ReturnedMetadata.Add(surface);
        return surface is not null;
    }
    public MapPatchFindResult FindPatches()
    {
        Alive();
        if (_found) throw new InvalidOperationException("probe discovery repeated");
        _found = true;
        Finds++;
        if (_source.FindException is { } exception) throw exception;
        var patches = new List<MapPatchRead>();
        var empty = new List<MapCoveredRange>(_source.CoveredEmpty);
        var unavailable = new List<MapPatchRead>(_source.Sentinels);
        foreach (MapPatchKey key in _source.DiscoveryKeys)
        {
            Reserve(key);
            MapPatchRead read = Serve(key);
            if (read.Status == MapPatchStatus.Present) patches.Add(read);
            else if (read.Status == MapPatchStatus.KnownEmpty) empty.Add(new(key.SurfaceId, new(key.SlotX, key.SlotZ, key.SlotX + 1, key.SlotZ + 1)));
            else unavailable.Add(read);
        }
        var result = new MapPatchFindResult(_source.FindStatus ?? (unavailable.Count == 0 ? MapFindStatus.Complete : MapFindStatus.Incomplete),
            Scope, SnapshotId, patches.AsReadOnly(), empty.AsReadOnly(), unavailable.AsReadOnly(), 0);
        if (result.Status == MapFindStatus.CapacityExceeded) result = result with
        {
            Patches = Array.Empty<MapPatchRead>(),
            KnownEmpty = Array.Empty<MapCoveredRange>(),
            Unavailable = Array.Empty<MapPatchRead>(),
        };
        return _source.FindFault is { } fault ? fault(result) : result;
    }
    public MapPatchRead ReadPatch(MapPatchKey key)
    {
        Found();
        Reserve(key);
        ExplicitReads.Add(key);
        if (_source.ReadException is { } exception) throw exception;
        MapPatchRead read = Serve(key);
        if (_source.CollateralReservation is { } collateral) _reserved.Add(collateral);
        return _source.ReadFault is { } fault ? fault(key, read) : read;
    }
    MapPatchRead Serve(MapPatchKey key)
    {
        _served.Add(key);
        MapPatchRead read = _source.Reads.TryGetValue(key, out MapPatchRead? value) ? value : new(key, MapPatchStatus.KnownEmpty, null, null, null, 0);
        MapSurfacePatch? patch = read.Patch?.Clone();
        if (patch is not null) ReturnedPatches.Add(patch);
        return read with { Patch = patch };
    }
    void Reserve(MapPatchKey key)
    {
        if (_reserved.Contains(key)) return;
        if (_reserved.Count >= Scope.Limits.MaxCandidatePatches) throw new MapSurfaceCapacityException();
        _reserved.Add(key);
    }
    public bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records)
    {
        Found();
        if (!_served.Contains(acquiredKey)) throw new InvalidOperationException("probe incident key not served");
        IncidentQueries.Add(acquiredKey);
        IReadOnlyList<MapRecordRef>? value = _source.Incidents.TryGetValue(acquiredKey, out var supplied) ? supplied : Array.Empty<MapRecordRef>();
        records = value is null ? null : Array.AsReadOnly(value.ToArray());
        return records is not null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        ReservedAtDispose = Array.AsReadOnly(_reserved.ToArray());
        _disposed = true;
        Disposals++;
    }
    void Alive() { if (_disposed) throw new ObjectDisposedException(nameof(R2AcquisitionProbeSession)); }
    void Found() { Alive(); if (!_found) throw new InvalidOperationException("probe requires discovery"); }
}

internal sealed class R2LegacyProbeSource : IMapSurfaceSource
{
    public string SnapshotId => "r2-legacy:fixed";
    public string RootSha256 => new('0', 64);
    internal int SurfaceGets { get; private set; }
    internal int Finds { get; private set; }
    internal int Reads { get; private set; }
    public IReadOnlyList<MapSurfaceRef> Surfaces { get { SurfaceGets++; return Array.Empty<MapSurfaceRef>(); } }
    public MapPatchRead ReadPatch(MapPatchKey key) { Reads++; return new(key, MapPatchStatus.KnownEmpty, null, null, null, 0); }
    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        Finds++;
        return new(MapFindStatus.Complete, scope, SnapshotId, Array.Empty<MapPatchRead>(), Array.Empty<MapCoveredRange>(), Array.Empty<MapPatchRead>(), 0);
    }
}
