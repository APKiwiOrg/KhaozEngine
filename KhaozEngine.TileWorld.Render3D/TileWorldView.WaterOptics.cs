using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    readonly TileWaterDepthMode _waterDepthMode;
    readonly ITileWorldWaterTriangleOpticsScene? _opticsScene;
    readonly TileWaterPublication _publication;
    readonly Func<RegionCoord, int, TileGroundSurfaceSnapshot?> _acceptedSurface;
    readonly Dictionary<RegionCoord, HashSet<RegionCoord>> _waterSourceRefusals = new();
    readonly Dictionary<(RegionCoord Region, int Plane), WaterAttempt> _waterAttempts = new();
    // Profile requests own their preparation stamps until the queue stops tracking that generation.
    readonly Dictionary<TileWorldBuildKey, (long Generation, TileWaterPreparation Water)> _pendingWater = new();
    readonly List<TileWorldBuildKey> _pendingRetired = new();
    readonly List<(RegionCoord Region, int Plane)> _waterRefresh = new();
    readonly List<(RegionCoord Region, int Plane)> _waterInvalidations = new();
    float _waterTileSize, _waterPlaneHeight;
    // The effective displacement, resolved once per flush. A refused look has zero padding and is uncertified.
    float _waterPadding;
    bool _waterCertified = true;
    // The scene's reason for an uncertified look, reported as the refusal of every uncertified water body.
    WaterOpticsSurfaceFailure _waterRefusal;
    readonly record struct WaterAttempt(long Revision, float TileSize, float PlaneHeight, float Padding, bool Certified)
    {
        // An uncertified latch waits only for a certifiable look. Edits elsewhere do not rebuild its terrain.
        internal bool IsStale(WaterAttempt current) => Certified ? this != current : current.Certified;
    }

    /// <summary>Bounded refusal diagnostics for resident region-planes. The read-only snapshot changes
    /// only when a preparation refusal changes or is resolved.</summary>
    public IReadOnlyList<TileWaterOpticsFailure> WaterOpticsFailures => _publication.Failures;

    /// <summary>Retained optical source-snapshot bytes, resident and awaiting publication. Zero outside
    /// <see cref="TileWaterDepthMode.AuthoredBed"/>. Optical fields themselves are reported by the scene.</summary>
    public TileWaterOpticsSourceBytes WaterOpticsSourceBytes
    {
        get
        {
            long resident = 0;
            foreach (RegionHandles handles in _loaded.Values)
                foreach (TileGroundSurfaceSnapshot? surface in handles.Surfaces)
                    resident = checked(resident + (surface?.RetainedBytes ?? 0));
            return new TileWaterOpticsSourceBytes(resident, _profileBuilds.ReadyBytes(PendingSurfaceBytes));
        }
    }

    static long PendingSurfaceBytes(TileProfileBuildOutput output) =>
        output is TileGroundBuildOutput ground ? ground.Ground.Surface?.RetainedBytes ?? 0 : 0;

    internal TileWaterDependencyRegistry WaterRegistry => _publication.Registry;
    internal int WaterRefusalCount => _publication.Refused.Count;
    internal int WaterSourceRefusalCount => _waterSourceRefusals.Count;
    internal int PendingWaterCount => _pendingWater.Count;

    /// <summary>The surface identities a resident field consumed, empty when none is resident.</summary>
    internal TileGroundSurfaceId[] WaterSourcesOf(RegionCoord region, int plane) =>
        _publication.Resident.TryGetValue((region, plane), out TileWaterGeneration? generation) ? generation.Sources : [];

    TileWaterSourceContext WaterSources => new(_doc, _catalogs, _mesher,
        _waterDepthMode == TileWaterDepthMode.AuthoredBed ? _publication.Registry : null);

    WaterAttempt CurrentWaterAttempt() =>
        new(_publication.Registry.Revision, _doc.TileSize, _doc.PlaneHeight, _waterPadding, _waterCertified);

    // The single authority for displacement. Non-throwing, so an uncertified look never blocks terrain.
    void ResolveWaterDisplacement()
    {
        if (_opticsScene is null)
        {
            _waterPadding = 0f;
            _waterCertified = true;
            return;
        }
        _waterCertified = _opticsScene.TryGetWaterOpticsPadding(_options.WaterLook, out float padding,
            out WaterOpticsSurfaceFailure reason);
        _waterPadding = _waterCertified ? padding : 0f;
        _waterRefusal = _waterCertified ? default : reason;
    }

    // The scene's padding refusal, mapped to its TileWorld kind. A reason without one reports
    // UncertifiedDisplacement.
    static TileWaterOpticsFailureKind UncertifiedKind(WaterOpticsSurfaceFailure reason) => reason switch
    {
        WaterOpticsSurfaceFailure.IncompleteCoverage => TileWaterOpticsFailureKind.IncompleteSurfaceCoverage,
        WaterOpticsSurfaceFailure.AmbiguousSurface => TileWaterOpticsFailureKind.AmbiguousSurface,
        WaterOpticsSurfaceFailure.CapacityExceeded => TileWaterOpticsFailureKind.SurfaceCapacityExceeded,
        WaterOpticsSurfaceFailure.UnrepresentableSurface => TileWaterOpticsFailureKind.UnrepresentableSurface,
        _ => TileWaterOpticsFailureKind.UncertifiedDisplacement,
    };

    bool DisplacementCurrent(TileWaterPreparation water) =>
        water.Padding == _waterPadding && water.Uncertified != _waterCertified &&
        (!water.Uncertified || water.Refusal is not { } refusal || refusal.Kind == UncertifiedKind(_waterRefusal));

    // All disk materialization happens here, before terrain capture and body/rim collection. Mesh audits still
    // perform strict corner reads and refuse genuinely absent coverage. The neighbours are optics-only sources,
    // so an uncertified look reads none of them and a failed read never costs terrain: it is memoised, the
    // dependency stays known-unloaded, and the strict reads refuse any water that needs it, naming it.
    void EnsureWaterSources(RegionCoord region)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed || !_waterCertified || _doc.Source is not { } source)
            return;
        float padding = _waterPadding;
        double margin = Math.Ceiling((double)padding / _doc.TileSize) + 1;
        if (!double.IsFinite(margin) || margin < 1 ||
            TileRegion.Size + 1 + 2 * margin > WaterOpticsTriangleField.MaxDimension)
            return;
        long minX = (long)region.OriginX - (long)margin;
        long minZ = (long)region.OriginZ - (long)margin;
        long maxX = (long)region.OriginX + TileRegion.Size + (long)margin;
        long maxZ = (long)region.OriginZ + TileRegion.Size + (long)margin;
        if (minX <= int.MinValue || minZ <= int.MinValue || maxX > int.MaxValue || maxZ > int.MaxValue) return;
        RegionCoord min = RegionCoord.Of((int)minX - 1, (int)minZ - 1);
        RegionCoord max = RegionCoord.Of((int)maxX, (int)maxZ);
        for (int z = min.Rz; z <= max.Rz; z++)
            for (int x = min.Rx; x <= max.Rx; x++)
            {
                var dependency = new RegionCoord(x, z);
                if (source.IsLoaded(dependency) || !source.IsKnown(dependency)) continue;
                // Requests sharing this unavailable dependency must not reopen it every flush. The memo is
                // owned by the regions that met it and released with the last of them or an availability change.
                if (_waterSourceRefusals.TryGetValue(dependency, out HashSet<RegionCoord>? owners))
                {
                    owners.Add(region);
                    continue;
                }
                try { source.EnsureLoaded(dependency); }
                catch (Exception error) when (error is TileWorldException or System.IO.IOException or UnauthorizedAccessException)
                {
                    _waterSourceRefusals[dependency] = new HashSet<RegionCoord> { region };
                    continue;
                }
                MarkRegionStreamed(dependency);
            }
    }

    void ReleaseWaterSourceRefusals(RegionCoord owner)
    {
        foreach (var entry in _waterSourceRefusals)
            if (entry.Value.Remove(owner) && entry.Value.Count == 0) _waterSourceRefusals.Remove(entry.Key);
    }

    TileWaterPreparation CaptureWater(RegionCoord region, int plane)
    {
        // Scene-depth water collects lazily at draw, per mesh, so its builds read no water at all.
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed)
            return new TileWaterPreparation([], new TileWaterSourceStamp(_doc, [], null), 0f, _options.WaterLook);
        float padding = _waterPadding;
        var key = (region, plane);
        WaterAttempt attempt = CurrentWaterAttempt();
        if (!_waterCertified) return CaptureUncertifiedWater(key, attempt);
        // A latch suppresses only the field attempt. Ground still builds and carries the kept refusal, so a
        // residency promotion changes the LOD while the unchanged field stays refused.
        if (_waterAttempts.TryGetValue(key, out WaterAttempt latched) && latched == attempt &&
            _publication.FailureOf(key) is { } kept)
            return CollectWater(key, padding, kept);
        // Latch the attempt. A recorded failure suppresses retries until an announced source or settings change. A throw
        // that records no failure retries on the next queued rebuild.
        _waterAttempts[key] = attempt;
        EnsureWaterSources(region);
        TileWaterPreparation prepared = CollectWater(key, padding, null);
        _waterAttempts.Remove(key);
        return prepared;
    }

    TileWaterPreparation CollectWater((RegionCoord Region, int Plane) key, float padding, TileWaterOpticsFailure? refusal)
    {
        WaterCollections++;
        IReadOnlyList<WaterPlane> collected = TileWaterPlanes.Collect(_doc, _catalogs, key.Region, key.Plane,
            _options.WaterLook);
        var planes = new WaterPlane[collected.Count];
        for (int i = 0; i < planes.Length; i++) planes[i] = collected[i];
        return new TileWaterPreparation(planes, new TileWaterSourceStamp(_doc,
            WaterDependencies(key.Region, key.Plane, planes), _publication.Registry), padding, _options.WaterLook,
            Refusal: refusal);
    }

    // Terrain only. No source materialization and no field. Water bodies latch on the refused state and become
    // refusals of the scene's reason when the group commits.
    TileWaterPreparation CaptureUncertifiedWater((RegionCoord Region, int Plane) key, WaterAttempt attempt)
    {
        TileWaterPreparation collected = CollectWater(key, 0f, null);
        WaterPlane[] planes = collected.Planes;
        if (planes.Length > 0) _waterAttempts[key] = attempt;
        else _waterAttempts.Remove(key);
        TileWaterOpticsFailure? refusal = planes.Length == 0 ? null
            : new TileWaterOpticsFailure(key.Region, key.Plane, planes[0].SurfaceY, UncertifiedKind(_waterRefusal));
        return collected with { Uncertified = true, Refusal = refusal };
    }

    // Body and rim inputs. Bed sources are tracked by surface identity and witness instead. A body touching a
    // region border reads rim corners that neighbouring regions can own, including derived planes.
    IEnumerable<TileWaterOpticsDependency> WaterDependencies(RegionCoord region, int plane, WaterPlane[] planes)
    {
        yield return new TileWaterOpticsDependency(region, null);
        yield return new TileWaterOpticsDependency(region, plane);
        if (planes.Length == 0) yield break;
        bool west = false, east = false, north = false, south = false;
        float tileSize = _doc.TileSize;
        foreach (WaterPlane p in planes)
        {
            double x0 = ((double)p.CenterX - p.HalfExtentX) / tileSize, x1 = ((double)p.CenterX + p.HalfExtentX) / tileSize;
            double z0 = -((double)p.CenterZ + p.HalfExtentZ) / tileSize, z1 = -((double)p.CenterZ - p.HalfExtentZ) / tileSize;
            west |= x0 < region.OriginX + 0.5;
            east |= x1 > (double)region.OriginX + TileRegion.Size - 0.5;
            south |= z0 < region.OriginZ + 0.5;
            north |= z1 > (double)region.OriginZ + TileRegion.Size - 0.5;
        }
        for (int dz = south ? -1 : 0; dz <= (north ? 1 : 0); dz++)
            for (int dx = west ? -1 : 0; dx <= (east ? 1 : 0); dx++)
            {
                RegionCoord rim = region.Offset(dx, dz);
                if (rim != region) yield return new TileWaterOpticsDependency(rim, null);
                for (int q = 0; q <= plane; q++)
                    if (rim != region || q != plane) yield return new TileWaterOpticsDependency(rim, q);
            }
    }

    bool WaterInputsCurrent(TileWaterPreparation water) =>
        water.Stamp.IsCurrent(_doc, _waterDepthMode == TileWaterDepthMode.AuthoredBed ? _publication.Registry : null) &&
        DisplacementCurrent(water);

    bool GroundInputsCurrent(TileGroundSurfaceSnapshot? surface, TileWaterPreparation water) =>
        WaterInputsCurrent(water) && (surface is not null) == GroundSurfaceRequested &&
        (surface is null || surface.SourceWitness.IsCurrent(_doc, _catalogs, _mesher));

    // Stage one source replacement with every dependent optical field. No GPU ownership escapes until all of the
    // group's uploads succeed and the source remains current.
    internal TileWaterPublication.Group? StageGround(RegionCoord region, int plane, TileGroundCpuBuild ground,
        TileWaterPreparation water)
    {
        if (!GroundInputsCurrent(ground.Surface, water)) return null;
        TileWaterPublication.Group group = _publication.Stage(region, plane, changesSource: true, ground.Surface, water,
            _acceptedSurface, WaterSources);
        group.Ground = ground;
        try
        {
            group.Mesh = ground.Mesh is null ? null : _scene.LoadMesh(ground.Mesh, _material);
            foreach (TileWaterPublication.Member member in group.Members) member.Staged?.SetLook(_options.WaterLook);
            if (GroundInputsCurrent(ground.Surface, water)) return group;
        }
        catch
        {
            DiscardGround(group);
            throw;
        }
        DiscardGround(group);
        return null;
    }

    internal void DiscardGround(TileWaterPublication.Group group)
    {
        if (group.Mesh is { } mesh) _scene.UnloadMesh(mesh);
        group.Mesh = null;
        _publication.Discard(group);
    }

    // Validate again immediately before the swap, after every upload of the transaction.
    bool SealGround(TileWaterPublication.Group group)
    {
        TileWaterPublication.Member own = group.Members[0];
        if (!GroundInputsCurrent(group.Ground!.Surface, own.Preparation)) return false;
        _publication.Revalidate(group, WaterInputsCurrent, WaterSources);
        return true;
    }

    void CommitGround(RegionHandles handles, TileWaterPublication.Group group)
    {
        int plane = group.Plane;
        MeshHandle? oldMesh = handles.Meshes[plane];
        handles.Meshes[plane] = group.Mesh;
        handles.Surfaces[plane] = group.Ground!.Surface;
        _publication.Commit(group, Queue);
        if (oldMesh is { } old) _scene.UnloadMesh(old);
    }

    bool PublishGround(RegionCoord region, int plane, RegionHandles handles, TileGroundCpuBuild ground,
        TileWaterPreparation water)
    {
        TileWaterPublication.Group? group = StageGround(region, plane, ground, water);
        if (group is not null && !SealGround(group))
        {
            DiscardGround(group);
            group = null;
        }
        if (group is null)
        {
            Queue(region, plane);
            return false;
        }
        CommitGround(handles, group);
        return true;
    }

    void WithdrawWater(RegionCoord region, int plane)
    {
        _publication.Withdraw((region, plane));
        if (_loaded.ContainsKey(region)) Queue(region, plane);
    }

    void WaterHeightChanged(RegionCoord region, int plane)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return;
        _publication.Registry.Changed(new TileWaterOpticsDependency(region, plane));
        InvalidateWaterSources();
    }

    void WaterAvailabilityChanged(RegionCoord region)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return;
        _publication.Registry.Changed(new TileWaterOpticsDependency(region, null));
        _waterSourceRefusals.Remove(region);
        InvalidateWaterSources();
    }

    void InvalidateWaterSources()
    {
        _waterInvalidations.Clear();
        TileWaterDependencyRegistry registry = _publication.Registry;
        foreach (var entry in _publication.Resident)
            if (!entry.Value.Preparation.Stamp.IsCurrent(_doc, registry) ||
                entry.Value.Availability?.IsCurrent(_doc, registry) == false) _waterInvalidations.Add(entry.Key);
        foreach (var entry in _publication.Refused)
            if (!entry.Value.Preparation.Stamp.IsCurrent(_doc, registry) ||
                entry.Value.Availability?.IsCurrent(_doc, registry) == false) _waterInvalidations.Add(entry.Key);
        foreach (var key in _waterInvalidations) WithdrawWater(key.Region, key.Plane);
    }

    // Run even on an otherwise empty flush. Mutable effective scene/look displacement and document
    // scale changes are preparation inputs. Colour-only changes reuse the existing field bytes.
    void RefreshWaterPreparation()
    {
        ResolveWaterDisplacement();
        float padding = _waterPadding;
        if (_waterDepthMode == TileWaterDepthMode.AuthoredBed)
        {
            if (_waterTileSize != _doc.TileSize || _waterPlaneHeight != _doc.PlaneHeight)
            {
                _waterTileSize = _doc.TileSize;
                _waterPlaneHeight = _doc.PlaneHeight;
                _publication.Registry.SettingsChanged();
                foreach (var entry in _loaded)
                    for (int plane = 0; plane < _planes; plane++) Queue(entry.Key, plane);
            }
            InvalidateWaterSources();
            WaterAttempt current = CurrentWaterAttempt();
            foreach (var entry in _waterAttempts)
                if (entry.Value.IsStale(current)) Queue(entry.Key.Region, entry.Key.Plane);
        }
        _waterRefresh.Clear();
        foreach (var entry in _publication.Resident)
        {
            if (!DisplacementCurrent(entry.Value.Preparation)) _waterRefresh.Add(entry.Key);
            else entry.Value.SetLook(_options.WaterLook);
        }
        foreach (var entry in _publication.Refused)
        {
            if (DisplacementCurrent(entry.Value.Preparation)) continue;
            // An uncertified refusal republishes with its terrain, through its latched attempt.
            if (entry.Value.Preparation.Uncertified) Queue(entry.Key.Region, entry.Key.Plane);
            else _waterRefresh.Add(entry.Key);
        }
        foreach (var key in _waterRefresh) RefreshWater(key, padding);
    }

    // A padding change rebuilds against unchanged accepted sources. A refused wider preparation may keep its old
    // generation only when that generation's certificates still contain the new displacement. Withdrawn water asks
    // for a ground rebuild only when no replacement or refusal was committed here.
    void RefreshWater((RegionCoord Region, int Plane) key, float padding)
    {
        _publication.Resident.TryGetValue(key, out TileWaterGeneration? old);
        bool withdrawn = old is not null && (!_waterCertified || !old.Covers(padding));
        if (withdrawn)
        {
            _publication.Withdraw(key);
            old = null;
        }
        bool committed = false;
        try
        {
            TileWaterPreparation prepared = CaptureWater(key.Region, key.Plane);
            TileWaterPublication.Group group = _publication.Stage(key.Region, key.Plane, changesSource: false, null,
                prepared, _acceptedSurface, WaterSources);
            TileWaterPublication.Member member = group.Members[0];
            member.Staged?.SetLook(_options.WaterLook);
            _publication.Revalidate(group, WaterInputsCurrent, WaterSources);
            if (member.Dropped || (member.Outcome.Failure is not null && old is not null))
            {
                _publication.Discard(group);
                if (member.Outcome.Failure is { } kept) _publication.RecordFailure(key, kept);
                // A kept refusal latches: only an announced source or settings change retries its field. A dropped
                // member is stale instead, so it stays unlatched and its queued rebuild attempts the field again.
                if (member.Dropped) Queue(key.Region, key.Plane);
                else _waterAttempts[key] = CurrentWaterAttempt();
                return;
            }
            _publication.Commit(group, Queue);
            committed = true;
        }
        finally
        {
            if (withdrawn && !committed && _loaded.ContainsKey(key.Region)) Queue(key.Region, key.Plane);
        }
    }

    void SweepWaterMetadata()
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return;
        _pendingRetired.Clear();
        foreach (var entry in _pendingWater)
            if (!_profileBuilds.IsTracked(entry.Key, entry.Value.Generation)) _pendingRetired.Add(entry.Key);
        foreach (TileWorldBuildKey key in _pendingRetired) _pendingWater.Remove(key);
        if (_pendingRetired.Count > 0) _publication.SweepDue = true;
        _pendingRetired.Clear();
        if (!_publication.SweepDue) return;
        _publication.Sweep(PendingStamps());
    }

    IEnumerable<TileWaterSourceStamp> PendingStamps()
    {
        foreach (var entry in _pendingWater) yield return entry.Value.Water.Stamp;
    }

    void TrackPendingWater(TileWorldBuildKey key, long generation, TileWaterPreparation water)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return;
        _pendingWater[key] = (generation, water);
        _publication.SweepDue = true;
    }
}
