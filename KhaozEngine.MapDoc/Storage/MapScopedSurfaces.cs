using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Storage;

public enum MapAcquireStatus { Complete, Incomplete, CapacityExceeded, NotRepresentable }

/// <summary>Immutable acquired facts. No consumer access rereads the producer or its current generation.</summary>
public sealed class MapScopedSurfaces
{
    internal Scene.MapPreparationReservation? PreparationOwnership { get; private set; }
    readonly MapAcquiredFacts _facts;
    internal long FactsContainerBytes => _facts.ContainerBytes;
    internal long MetadataBytes => checked(256L + Surfaces.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) + 128L * Witness.Present.Count +
        64L * (Witness.Records.Count + Witness.KnownEmpty.Count + Witness.Unavailable.Count) +
        8L * Identity.AssetSha256.Count + 4L * Scope.Roles.Count + 8L * (Scope.SpaceIds?.Count ?? 0));
    internal MapEffectiveContext? EffectiveContext { get; private init; }
    MapScopedSurfaces(MapScopedAcquisition.Result result, MapSurfaceScope scope, IReadOnlyList<string> assets,
        int queryPolicyVersion, int buildPolicyVersion)
    {
        _facts = result.Facts;
        Status = result.Status;
        Detail = result.Detail;
        Capacity = result.Capacity;
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
    public Scene.MapSceneCapacity? Capacity { get; }
    public MapSurfaceScope Scope { get; }
    public IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    public MapCoverageWitness Witness { get; }
    public MapScopedIdentity Identity { get; }
    public MapReadWitness ReadWitness { get; }
    public int PagesRead { get; }
    public int RecordReads { get; }
    internal int PatchClones { get; private set; }
    /// <summary>True only for <see cref="CompleteView"/>, whose <see cref="Surfaces"/> lists every surface its set
    /// declares. A scoped acquisition lists only the surfaces it captured, so a surface it does not list may exist.</summary>
    internal bool ListsEverySurface { get; private init; }

    /// <summary>The query policy version of effective acquisitions, the shared seat query and the typed refusals that
    /// record it without acquiring.</summary>
    internal const int EffectiveQueryPolicyVersion = 2;
    /// <summary>The build policy version of effective acquisitions and the shared seat query.</summary>
    internal const int EffectiveBuildPolicyVersion = 2;

    public static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256,
        int queryPolicyVersion = 1, int buildPolicyVersion = 1) =>
        Acquire(source, scope, assetSha256, queryPolicyVersion, buildPolicyVersion, null);
    internal static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, MapAcquisitionWork work) =>
        Acquire(source, scope, assetSha256, 1, 1, work);
    static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256,
        int queryPolicyVersion, int buildPolicyVersion, MapAcquisitionWork? work)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Scene.MapSceneAdmission.Current is { } active && !Scene.MapSceneAdmission.Supports(source))
            throw new Scene.MapSceneSourceException(Scene.MapSceneStatus.Invalid, "Surface source cannot participate in scene admission.");
        if (source is not IMapSurfaceAcquisitionSource capable)
            throw new MapDocumentException("surface source does not support bounded scoped acquisition");
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(assetSha256);
        scope.Validate();
        var admission = scope.SceneAdmission ?? Scene.MapSceneAdmission.Current;
        using var diagnostics = admission?.AcquisitionDiagnostics?.Enter(Scene.MapAcquisitionStage.View);
        admission?.BeforeScope(source);
        using var admitted = admission?.Enter();
        using var preparation = admission?.BeginPreparation();
        admission?.ReserveBytes(checked(4L * scope.Roles.Count + 8L * (scope.SpaceIds?.Count ?? 0) + 8L * assetSha256.Count));
        MapSurfaceScope request = scope with
        {
            Roles = Array.AsReadOnly(scope.Roles.ToArray()),
            SpaceIds = scope.SpaceIds is null ? null : Array.AsReadOnly(scope.SpaceIds.ToArray()),
            Limits = scope.Limits with { },
            SceneAdmission = admission,
        };
        string[] assets = assetSha256.ToArray();
        foreach (string asset in assets) MapSurfaceStorageLayout.RequireDigest(asset);
        if (queryPolicyVersion < 1 || buildPolicyVersion < 1) throw new ArgumentOutOfRangeException(nameof(queryPolicyVersion), "invalid acquisition policy version");
        var context = (source as IMapEffectiveSurfaceContext)?.EffectiveContext;
        var facts = context is null ? MapScopedAcquisition.Run(capable, request, work)
            : MapEffectiveAcquisition.Run(capable, request, work, context);
        if (admission?.AcquisitionDiagnostics is { } counts)
            foreach (var read in facts.Facts.Reads) counts.Key(source.SnapshotId, read.Key);
        admission?.ReserveBytes(checked(256L + facts.Facts.Surfaces.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) + 128L * facts.Facts.Present.Count() +
            64L * (facts.Facts.Records.Count() + facts.Facts.KnownEmpty.Count() + facts.Facts.Unavailable.Count())));
        var view = new MapScopedSurfaces(facts, request, assets,
            context is null ? queryPolicyVersion : 2, context is null ? buildPolicyVersion : 2)
        { EffectiveContext = context };
        view.PreparationOwnership = preparation?.Complete(new Scene.MapSceneInventory().View(view, includeContext: false));
        return view;
    }
    public static MapScopedSurfaces CompleteView(MapSurfaceSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        var facts = new MapAcquiredFacts();
        foreach (MapSurfaceRef surface in set.Refs.OrderBy(s => s.Id, StringComparer.Ordinal)) facts.AddSurface(surface);
        foreach (var entry in set.Patches)
        {
            if (entry.Key != entry.Value.Key || !facts.TrySurface(entry.Key.SurfaceId, out _))
                throw new MapDocumentException("resident patch has an undeclared surface or mismatched key");
            facts.AddRead(new(entry.Key, MapPatchStatus.Present, entry.Value, MapSurfaceSemantics.PatchDigest(entry.Value), null, 0));
        }
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(128L + 256L * facts.Reads.Count()));
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
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(512L + facts.Surfaces.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) + 128L * facts.Present.Count() +
            64L * (facts.Records.Count() + facts.KnownEmpty.Count())));
        var view = new MapScopedSurfaces(new(MapAcquireStatus.Complete, null, facts, snapshot, rootSha256, 0, 0), scope, Array.Empty<string>(), 1, 1)
        {
            ListsEverySurface = true,
        };
        view.PreparationOwnership = preparation?.Complete(new Scene.MapSceneInventory().View(view, includeContext: false));
        return view;
    }
    internal static MapScopedSurfaces CompleteEffective(MapEffectiveContext context, MapQueryLimits? queryLimits = null)
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        var facts = new MapAcquiredFacts();
        foreach (var surface in context.Surfaces.Refs) facts.AddSurface(surface);
        foreach (var entry in context.Surfaces.Patches)
            facts.AddRead(new(entry.Key, MapPatchStatus.Present, entry.Value, MapSurfaceSemantics.PatchDigest(entry.Value), null, 0));
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(128L + 256L * context.Surfaces.Patches.Count));
        var localScope = ResidentScope(facts);
        var corners = context.Surfaces.Patches.Values.SelectMany(p =>
        {
            var surface = context.Surfaces.Refs.Single(s => s.Id == p.Key.SurfaceId);
            long x = checked(p.Key.SlotX * 64 + p.CellMinX), z = checked(p.Key.SlotZ * 64 + p.CellMinZ);
            return new[] { MapLatticeAddress.Corner(x, z), MapLatticeAddress.Corner(x + p.Width, z),
                MapLatticeAddress.Corner(x + p.Width, z + p.Depth), MapLatticeAddress.Corner(x, z + p.Depth) }
                .Select(a => { var point = surface.Frame.WorldXz(a); return context.Xz(surface.Id, point); });
        }).ToArray();
        var frame = corners.Length == 0 ? WorldFrame.Origin : WorldFrame.Nearest(corners[0].X.ToSingle(), corners[0].Z.ToSingle());
        long ax = (long)frame.X * (long)WorldFrame.Grid, az = (long)frame.Z * (long)WorldFrame.Grid;
        var scope = localScope with
        {
            Limits = queryLimits ?? localScope.Limits,
            Frame = frame,
            LocalMin = corners.Length == 0 ? Vector2.Zero : new(Outward(corners.Min(c => c.X.Floor()) - ax, true), Outward(corners.Min(c => c.Z.Floor()) - az, true)),
            LocalMax = corners.Length == 0 ? Vector2.Zero : new(Outward(corners.Max(c => c.X.Ceiling()) - ax, false), Outward(corners.Max(c => c.Z.Ceiling()) - az, false)),
        };
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(512L + facts.Surfaces.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) + 128L * facts.Present.Count() +
            64L * (facts.Records.Count() + facts.KnownEmpty.Count())));
        var view = new MapScopedSurfaces(new(MapAcquireStatus.Complete, null, facts, "effective:" + context.Digest, context.Digest, 0, 0), scope, new[] { context.Digest },
            EffectiveQueryPolicyVersion, EffectiveBuildPolicyVersion)
        { EffectiveContext = context, ListsEverySurface = true };
        view.PreparationOwnership = preparation?.Complete(new Scene.MapSceneInventory().View(view, includeContext: false));
        return view;
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
    internal bool TryRecord(MapRecordRef r, out MapTopologyRecord? record, out MapPatchStatus status, bool borrow) =>
        borrow ? _facts.TryRecord(r, out record, out status) : _facts.CopyRecord(r, out record, out status);
    public IEnumerable<MapTopologyRecord> RecordsIn(MapPatchKey key) => _facts.CopyRecords(key);
    /// <summary>True when the acquisition recorded a read of <paramref name="key"/>, whatever its status.</summary>
    internal bool HasRead(MapPatchKey key) => _facts.HasRead(key);
    internal bool TryAcquiredPatch(MapPatchKey key, out MapSurfacePatch? patch, out MapPatchStatus status)
    {
        patch = _facts.OwnedPatch(key);
        status = _facts.Status(key);
        if (EffectiveContext is not null && status == MapPatchStatus.Unloaded && _facts.IsKnownEmpty(key)) status = MapPatchStatus.KnownEmpty;
        if (EffectiveContext is not null && ListsEverySurface && patch is null &&
            HasEffectiveSurface(key.SurfaceId) && !EffectiveContext.Surfaces.Patches.ContainsKey(key)) status = MapPatchStatus.KnownEmpty;
        return status is MapPatchStatus.Present or MapPatchStatus.KnownEmpty;
    }
    bool HasEffectiveSurface(string id)
    {
        var surfaces = EffectiveContext!.Surfaces.Refs;
        for (int i = 0; i < surfaces.Count; i++) if (surfaces[i].Id == id) return true;
        return false;
    }
}
