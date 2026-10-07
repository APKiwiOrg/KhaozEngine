using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Storage;

public enum MapAcquireStatus { Complete, Incomplete, CapacityExceeded, NotRepresentable }

/// <summary>Immutable acquired facts. No consumer access rereads the producer or its current generation.</summary>
public sealed class MapScopedSurfaces
{
    readonly MapAcquiredFacts _facts;
    MapScopedSurfaces(MapScopedAcquisition.Result result, MapSurfaceScope scope, IReadOnlyList<string> assets,
        int queryPolicyVersion, int buildPolicyVersion)
    {
        _facts = result.Facts;
        Status = result.Status;
        Detail = result.Detail;
        Scope = scope;
        Surfaces = Array.AsReadOnly(_facts.Surfaces.Select(MapAcquiredFacts.CopySurface).ToArray());
        bool complete = Status == MapAcquireStatus.Complete;
        Witness = new(result.SnapshotId, scope.Digest, scope.Frame, Surfaces.Select(s => s.Id), _facts.Present,
            _facts.KnownEmpty, _facts.Records, _facts.Unavailable, complete);
        Identity = new(result.RootSha256, Witness.ScopeDigest, scope.Frame, Witness.Present, Witness.Records,
            Witness.KnownEmpty, assets, queryPolicyVersion, buildPolicyVersion, complete);
        ReadWitness = new(result.SnapshotId, Identity.Digest, scope.Frame, queryPolicyVersion, complete);
        PagesRead = result.PagesRead;
        RecordReads = result.RecordReads;
    }
    public MapAcquireStatus Status { get; }
    public string? Detail { get; }
    public MapSurfaceScope Scope { get; }
    public IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    public MapCoverageWitness Witness { get; }
    public MapScopedIdentity Identity { get; }
    public MapReadWitness ReadWitness { get; }
    public int PagesRead { get; }
    public int RecordReads { get; }
    internal int PatchClones { get; private set; }

    public static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256,
        int queryPolicyVersion = 1, int buildPolicyVersion = 1) =>
        Acquire(source, scope, assetSha256, queryPolicyVersion, buildPolicyVersion, null);
    internal static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, MapAcquisitionWork work) =>
        Acquire(source, scope, assetSha256, 1, 1, work);
    static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256,
        int queryPolicyVersion, int buildPolicyVersion, MapAcquisitionWork? work)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is not IMapSurfaceAcquisitionSource capable)
            throw new MapDocumentException("surface source does not support bounded scoped acquisition");
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(assetSha256);
        scope.Validate();
        MapSurfaceScope request = scope with
        {
            Roles = Array.AsReadOnly(scope.Roles.ToArray()),
            SpaceIds = scope.SpaceIds is null ? null : Array.AsReadOnly(scope.SpaceIds.ToArray()),
            Limits = scope.Limits with { },
        };
        string[] assets = assetSha256.ToArray();
        foreach (string asset in assets) MapSurfaceStorageLayout.RequireDigest(asset);
        if (queryPolicyVersion < 1 || buildPolicyVersion < 1) throw new ArgumentOutOfRangeException(nameof(queryPolicyVersion), "invalid acquisition policy version");
        return new(MapScopedAcquisition.Run(capable, request, work), request, assets, queryPolicyVersion, buildPolicyVersion);
    }
    public static MapScopedSurfaces CompleteView(MapSurfaceSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var facts = new MapAcquiredFacts();
        foreach (MapSurfaceRef surface in set.Refs.OrderBy(s => s.Id, StringComparer.Ordinal)) facts.AddSurface(surface);
        foreach (var entry in set.Patches)
        {
            if (entry.Key != entry.Value.Key || !facts.TrySurface(entry.Key.SurfaceId, out _))
                throw new MapDocumentException("resident patch has an undeclared surface or mismatched key");
            facts.AddRead(new(entry.Key, MapPatchStatus.Present, entry.Value, MapSurfaceSemantics.PatchDigest(entry.Value), null, 0));
        }
        MapSurfaceScope scope = ResidentScope(facts);
        var root = new MapDocument();
        root.Surfaces.Refs.AddRange(facts.Surfaces);
        string rootSha256 = MapSurfaceSemantics.RootDigest(root);
        string snapshot = "complete:" + MapCanonical.HashHex(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/complete-view/1"); w.WriteStringValue(rootSha256);
            foreach (var patch in facts.Present)
            {
                w.WriteStringValue(patch.Key.SurfaceId); w.WriteNumberValue(patch.Key.SlotX); w.WriteNumberValue(patch.Key.SlotZ); w.WriteStringValue(patch.Value);
            }
            w.WriteEndArray();
        });
        return new(new(MapAcquireStatus.Complete, null, facts, snapshot, rootSha256, 0, 0), scope, Array.Empty<string>(), 1, 1);
    }
    static MapSurfaceScope ResidentScope(MapAcquiredFacts facts)
    {
        var corners = new List<MapExactXz>();
        foreach (MapPatchRead read in facts.Reads)
        {
            facts.TrySurface(read.Key.SurfaceId, out MapSurfaceRef? surface);
            try
            {
                long x = checked(read.Key.SlotX * 64), z = checked(read.Key.SlotZ * 64);
                corners.Add(surface!.Frame.WorldXz(MapLatticeAddress.Corner(x, z)));
                corners.Add(surface.Frame.WorldXz(MapLatticeAddress.Corner(checked(x + 64), checked(z + 64))));
            }
            catch (OverflowException) { throw new MapExactOverflowException(); }
        }
        Vector2 min = corners.Count == 0 ? Vector2.Zero : new(Outward(corners.Min(c => c.X.Floor()), lower: true), Outward(corners.Min(c => c.Z.Floor()), lower: true));
        Vector2 max = corners.Count == 0 ? Vector2.Zero : new(Outward(corners.Max(c => c.X.Ceiling()), lower: false), Outward(corners.Max(c => c.Z.Ceiling()), lower: false));
        return new(WorldFrame.Origin, min, max, null, null, Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());
    }
    static float Outward(long coordinate, bool lower)
    {
        float value = coordinate;
        int comparison = MapExactValue.FromSingle(value).CompareTo(new(coordinate, 1));
        return lower && comparison > 0 ? MathF.BitDecrement(value) : !lower && comparison < 0 ? MathF.BitIncrement(value) : value;
    }
    public MapPatchRead Patch(MapPatchKey key)
    {
        MapPatchRead copy = _facts.CopyPatch(key);
        if (copy.Patch is not null) PatchClones++;
        return copy;
    }
    public bool TryRecord(MapRecordRef r, out MapTopologyRecord? record, out MapPatchStatus status) => _facts.CopyRecord(r, out record, out status);
    public IEnumerable<MapTopologyRecord> RecordsIn(MapPatchKey key) => _facts.CopyRecords(key);
    internal bool TryAcquiredPatch(MapPatchKey key, out MapSurfacePatch? patch, out MapPatchStatus status)
    {
        patch = _facts.OwnedPatch(key);
        status = _facts.Status(key);
        return status is MapPatchStatus.Present or MapPatchStatus.KnownEmpty;
    }
}
