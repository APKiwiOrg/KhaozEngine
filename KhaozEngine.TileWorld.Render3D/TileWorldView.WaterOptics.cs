using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    readonly TileWaterDepthMode _waterDepthMode;
    readonly ITileWorldWaterOpticsScene? _opticsScene;
    readonly Dictionary<TileWaterOpticsDependency, long> _waterEpochs = new();
    readonly Dictionary<RegionCoord, long> _waterSourceRefusals = new();
    readonly Dictionary<(RegionCoord Region, int Plane), TileWaterOpticsFailure> _waterFailures = new();
    readonly Dictionary<(RegionCoord Region, int Plane), WaterAttempt> _waterAttempts = new();
    readonly List<(RegionCoord Region, int Plane)> _waterRefresh = new();
    readonly List<(RegionCoord Region, int Plane)> _waterInvalidations = new();
    IReadOnlyList<TileWaterOpticsFailure> _waterFailureSnapshot = Array.Empty<TileWaterOpticsFailure>();
    long _waterRevision;
    float _waterTileSize, _waterPlaneHeight;
    readonly record struct WaterAttempt(long Revision, float TileSize, float PlaneHeight, float Padding);

    /// <summary>Bounded refusal diagnostics for resident region-planes. The read-only snapshot changes
    /// only when a preparation refusal changes or is resolved.</summary>
    public IReadOnlyList<TileWaterOpticsFailure> WaterOpticsFailures => _waterFailureSnapshot;

    void RebuildWaterFailures() => _waterFailureSnapshot = Array.AsReadOnly(
        new List<TileWaterOpticsFailure>(_waterFailures.Values).ToArray());

    void ForgetWaterFailure(RegionCoord region, int plane)
    {
        if (_waterFailures.Remove((region, plane))) RebuildWaterFailures();
    }

    void RecordWaterFailure(TileWaterOpticsFailure failure)
    {
        var key = (failure.Region, failure.Plane);
        if (_waterFailures.TryGetValue(key, out TileWaterOpticsFailure old) && old == failure) return;
        _waterFailures[key] = failure;
        RebuildWaterFailures();
    }

    WaterAttempt CurrentWaterAttempt(float padding) => new(_waterRevision, _doc.TileSize, _doc.PlaneHeight, padding);

    // All disk materialization happens here, before terrain capture and body/rim collection. The
    // producer still performs the strict corner read and refuses genuinely absent coverage.
    void EnsureWaterSources(RegionCoord region)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed || _doc.Source is not { } source) return;
        float padding = _opticsScene!.GetWaterOpticsPadding(_options.WaterLook);
        double margin = Math.Ceiling((double)padding / _doc.TileSize) + 1;
        if (!double.IsFinite(margin) || margin < 1 || TileRegion.Size + 1 + 2 * margin > WaterOpticsField.MaxDimension)
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
                var availability = new TileWaterOpticsDependency(dependency, null);
                long epoch = _waterEpochs.GetValueOrDefault(availability);
                if (_waterSourceRefusals.TryGetValue(dependency, out long refused) && refused == epoch) continue;
                try { source.EnsureLoaded(dependency); }
                catch
                {
                    // Requests sharing this unavailable dependency must not reopen it every flush.
                    // The source error still propagates on its first preparation attempt.
                    _waterSourceRefusals[dependency] = epoch;
                    throw;
                }
                MarkRegionStreamed(dependency);
            }
    }

    TileWaterPreparation? CaptureWater(RegionCoord region, int plane)
    {
        float padding = _opticsScene?.GetWaterOpticsPadding(_options.WaterLook) ?? 0f;
        var key = (region, plane);
        WaterAttempt attempt = CurrentWaterAttempt(padding);
        if (_waterAttempts.TryGetValue(key, out WaterAttempt refused) && refused == attempt) return null;
        // Latch even a throwing I/O request. Only an announced source/settings change retries it.
        _waterAttempts[key] = attempt;
        EnsureWaterSources(region);
        IReadOnlyList<WaterPlane> collected = TileWaterPlanes.Collect(_doc, _catalogs, region, plane, _options.WaterLook);
        var planes = new WaterPlane[collected.Count];
        var fields = new List<TileWaterOpticsBuild>();
        var levels = new HashSet<float>();
        var dependencies = new HashSet<TileWaterOpticsDependency>
        {
            new(region, null), new(region, plane),
        };
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = collected[i];
            if (_waterDepthMode != TileWaterDepthMode.AuthoredBed || !levels.Add(planes[i].SurfaceY)) continue;
            if (!TileWaterOptics.TryBuild(_doc, region, plane, planes[i].SurfaceY, padding, out TileWaterOpticsBuild build,
                out TileWaterOpticsFailure failure))
            {
                _waterAttempts[key] = CurrentWaterAttempt(padding);
                RecordWaterFailure(failure);
                return null;
            }
            fields.Add(build);
            foreach (TileWaterOpticsDependency dependency in build.Dependencies) dependencies.Add(dependency);
        }
        _waterAttempts.Remove(key);
        ForgetWaterFailure(region, plane);
        return new TileWaterPreparation(planes, fields.ToArray(),
            new TileWaterSourceStamp(_doc, dependencies, _waterEpochs), padding, _options.WaterLook);
    }

    bool WaterInputsCurrent(TileWaterPreparation water) =>
        water.Stamp.IsCurrent(_doc, _waterEpochs) &&
        water.Padding == (_opticsScene?.GetWaterOpticsPadding(_options.WaterLook) ?? 0f);

    // No GPU ownership escapes until both uploads succeed and the source remains current.
    bool UploadGround(GltfMesh? mesh, TileWaterPreparation water, out MeshHandle? replacement,
        out TileWaterGeneration? generation)
    {
        replacement = null;
        generation = null;
        if (!WaterInputsCurrent(water)) return false;
        try
        {
            generation = new TileWaterGeneration(water, _opticsScene);
            replacement = mesh is null ? null : _scene.LoadMesh(mesh, _material);
            generation.SetLook(_options.WaterLook);
            if (WaterInputsCurrent(water)) return true;
        }
        catch
        {
            generation?.Dispose();
            generation = null;
            if (replacement is { } unpublished) _scene.UnloadMesh(unpublished);
            replacement = null;
            throw;
        }
        if (replacement is { } stale) _scene.UnloadMesh(stale);
        replacement = null;
        generation?.Dispose();
        generation = null;
        return false;
    }

    // A later plane's upload can announce a source change while earlier planes are staged.
    // Revalidate the whole staged region immediately before its first publication. Source arrivals
    // consumed by this capture must not cause a redundant upload on the first ordinary draw.
    void SealGameplayWater(RegionCoord region, MeshHandle?[] meshes, TileWaterGeneration?[] water)
    {
        for (int plane = 0; plane < water.Length; plane++)
        {
            if (water[plane] is not { } generation) continue;
            if (!WaterInputsCurrent(generation.Preparation))
            {
                if (meshes[plane] is { } stale) _scene.UnloadMesh(stale);
                meshes[plane] = null;
                generation.Dispose();
                water[plane] = null;
                Queue(region, plane);
                continue;
            }
            if (_dirty.Remove((region, plane))) _dirtyOrder.Remove((region, plane));
        }
    }

    bool PublishGround(RegionCoord region, int plane, RegionHandles handles, GltfMesh? mesh,
        TileWaterPreparation water)
    {
        _water.EnsureCapacity(_water.Count + 1);
        if (!UploadGround(mesh, water, out MeshHandle? replacement, out TileWaterGeneration? generation))
        {
            Queue(region, plane);
            return false;
        }
        MeshHandle? oldMesh = handles.Meshes[plane];
        _water.TryGetValue((region, plane), out TileWaterGeneration? oldWater);
        handles.Meshes[plane] = replacement;
        _water[(region, plane)] = generation!;
        if (oldMesh is { } old) _scene.UnloadMesh(old);
        oldWater?.Dispose();
        return true;
    }

    void WithdrawWaterGround(RegionCoord region, int plane, RegionHandles handles)
    {
        if (_water.Remove((region, plane), out TileWaterGeneration? old)) old.Dispose();
        if (handles.Meshes[plane] is { } mesh) _scene.UnloadMesh(mesh);
        handles.Meshes[plane] = null;
        InvalidateGround(region, handles, plane);
    }

    void WaterHeightChanged(RegionCoord region, int plane)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return;
        _waterEpochs[new TileWaterOpticsDependency(region, plane)] = ++_waterRevision;
        InvalidateWaterSources();
    }

    void WaterAvailabilityChanged(RegionCoord region)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return;
        _waterEpochs[new TileWaterOpticsDependency(region, null)] = ++_waterRevision;
        _waterSourceRefusals.Remove(region);
        InvalidateWaterSources();
    }

    void InvalidateWaterSources()
    {
        _waterInvalidations.Clear();
        foreach (var entry in _water)
            if (!entry.Value.Preparation.Stamp.IsCurrent(_doc, _waterEpochs)) _waterInvalidations.Add(entry.Key);
        foreach (var key in _waterInvalidations)
            if (_loaded.TryGetValue(key.Region, out RegionHandles? handles))
            {
                WithdrawWaterGround(key.Region, key.Plane, handles);
                Queue(key.Region, key.Plane);
            }
    }

    // Run even on an otherwise empty flush. Mutable effective scene/look displacement and document
    // scale changes are preparation inputs. Colour-only changes reuse the existing field bytes.
    void RefreshWaterPreparation()
    {
        if (_waterDepthMode == TileWaterDepthMode.AuthoredBed)
        {
            if (_waterTileSize != _doc.TileSize || _waterPlaneHeight != _doc.PlaneHeight)
            {
                _waterTileSize = _doc.TileSize;
                _waterPlaneHeight = _doc.PlaneHeight;
                ++_waterRevision;
                foreach (var entry in _loaded)
                    for (int plane = 0; plane < _planes; plane++) Queue(entry.Key, plane);
            }
            InvalidateWaterSources();
            foreach (var entry in _waterAttempts)
                if (entry.Value != CurrentWaterAttempt(_opticsScene!.GetWaterOpticsPadding(_options.WaterLook)))
                    Queue(entry.Key.Region, entry.Key.Plane);
        }
        _waterRefresh.Clear();
        foreach (var entry in _water)
        {
            float padding = _opticsScene?.GetWaterOpticsPadding(_options.WaterLook) ?? 0f;
            if (entry.Value.Padding != padding) _waterRefresh.Add(entry.Key);
            else entry.Value.SetLook(_options.WaterLook);
        }
        foreach (var key in _waterRefresh)
        {
            TileWaterGeneration old = _water[key];
            if (!old.Covers(_opticsScene!.GetWaterOpticsPadding(_options.WaterLook), _doc.TileSize))
            {
                WithdrawWaterGround(key.Region, key.Plane, _loaded[key.Region]);
                Queue(key.Region, key.Plane);
                continue;
            }
            TileWaterPreparation? prepared = CaptureWater(key.Region, key.Plane);
            if (prepared is null) continue;
            TileWaterGeneration? next = new TileWaterGeneration(prepared, _opticsScene);
            try
            {
                if (!WaterInputsCurrent(prepared) ||
                    !_water.TryGetValue(key, out TileWaterGeneration? current) || !ReferenceEquals(current, old))
                {
                    if (_water.TryGetValue(key, out TileWaterGeneration? retained) && ReferenceEquals(retained, old) &&
                        !old.Covers(_opticsScene?.GetWaterOpticsPadding(_options.WaterLook) ?? 0f, _doc.TileSize))
                        WithdrawWaterGround(key.Region, key.Plane, _loaded[key.Region]);
                    Queue(key.Region, key.Plane);
                    continue;
                }
                _water[key] = next;
                next = null;
                old.Dispose();
            }
            finally { next?.Dispose(); }
        }
    }
}
