using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class WaterRenderer
{
    readonly Dictionary<WaterOpticsHandle, IGpuResourceSet> _opticsSets = new();
    readonly HashSet<WaterOpticsHandle> _frameFields = new();
    readonly List<WaterOpticsHandle> _unusedFields = new();
    PreparedPlane[] _preparedPlanes = Array.Empty<PreparedPlane>();
    IGpuResourceSet[] _planeSets = Array.Empty<IGpuResourceSet>();
    IGpuBuffer? _boundUbo;
    bool _opticsPrepared;
    // Whether the last viewport preparation queued bound water. Such a frame stays strict even if recording
    // later sees no bound plane.
    bool _preparedBound;
    int _preparedPlaneCount;
    WaterBathymetry? _preparedBathymetry;
    int _preparedBathymetryRevision;
    float _preparedTime;
    WaterGridMode _preparedGridMode;
    OceanInputs _preparedOcean;

    readonly record struct OceanInputs(Vector4 Spectrum, Vector4 Swell, Vector4 Shape,
        int Seed, int Cascades, int Resolution, Vector4 Focus, int Sectors, Vector3 Rotation, Vector4 WarpFoam)
    {
        internal static OceanInputs From(WaterSeaState s) => new(
            new(s.WindSpeed, s.WindDirectionDegrees, s.FetchKilometres, s.DepthMetres),
            new(s.DirectionalSpread, s.SwellAmount, s.SwellDirectionDegrees, s.SmallWaveCutoffMetres),
            new(s.Choppiness, s.CascadeTileMetres, s.CascadeTileRatio, s.FoamGain),
            s.Seed, s.CascadeCount, s.CascadeResolution,
            new(s.OnshoreFocusPoint, s.OnshoreFocusStrength, s.FoamJacobianBias), s.OnshoreFocusSectors,
            s.CascadeRotationDegrees, new(s.DomainWarpMetres, s.DomainWarpWavelengthMetres, s.FoamDissipationPerSecond, 0f));
    }

    // A look remains mutable. Store its resolved displacement values, never the bag's identity.
    readonly record struct PreparedPlane(Vector4 Position, float HalfZ, WaterOpticsHandle Field,
        Vector3 Origin, Vector2 Size, WaterWaveSource Source, Vector4 Swell, Vector4 Shape, int OpticalMode, PlaneRoute Geometry)
    {
        internal static PreparedPlane From(in WaterPlane plane, WaterSettings effective, int mode, PlaneRoute geometry) => new(
            new(plane.CenterX, plane.SurfaceY, plane.CenterZ, plane.HalfExtentX), plane.HalfExtentZ,
            plane.Optics.Field, plane.Optics.Origin, plane.Optics.Size, effective.WaveSource,
            new(effective.SwellAmplitude, effective.SwellWavelength, effective.SwellDirectionDegrees, effective.SwellSpreadDegrees),
            new(effective.SwellSteepness, effective.SwellSpeed, effective.SwellComponents, effective.SwellSeed), mode, geometry);
    }

    static GpuResourceLayoutElement[] AppendOpticsLayout(GpuResourceLayoutElement[] prefix)
    {
        Array.Resize(ref prefix, prefix.Length + 2);
        prefix[^2] = new("OpticsTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        prefix[^1] = new("OpticsSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        return prefix;
    }

    IGpuResourceSet CreateWaterSet(RenderResources targets, IGpuTexture optics) =>
        _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
            _bathymetry.Texture, _bathymetry.Sampler, _ocean.Map, _ocean.Sampler,
            targets.DepthColorTex, _gd.PointSampler, new GpuBufferRange(_ubo!, 0, SlotBytes),
            _atmosphere.Uniforms, _atmosphere.SkyView, _atmosphere.SkySampler, optics, _gd.PointSampler));

    bool TargetsMatch(RenderResources targets) => ReferenceEquals(_bound, targets)
        && _boundGen == targets.Generation && ReferenceEquals(_boundUbo, _ubo)
        && ReferenceEquals(_boundMap, _ocean.Map) && ReferenceEquals(_boundBathy, _bathymetry.Texture);

    void RetireWaterSets()
    {
        if (_set is not null) _retired.Retire(_set);
        _set = null;
        foreach (IGpuResourceSet set in _opticsSets.Values) _retired.Retire(set);
        _opticsSets.Clear();
        _bound = null;
    }

    internal void UnloadOpticsField(WaterOpticsHandle handle)
    {
        Optics.Unload(handle);
        RetireOpticsSet(handle);
    }

    void RetireOpticsSet(WaterOpticsHandle handle)
    {
        if (_opticsSets.Remove(handle, out IGpuResourceSet? set)) _retired.Retire(set);
    }

    WaterOpticsResources.Entry ResolveOptics(WaterOpticsHandle handle)
    {
        try { return Optics.Resolve(handle); }
        catch (ArgumentException) { RetireOpticsSet(handle); throw; }
    }

    // Also serves the existing unbound standalone renderer. Bound draws never enter this fallback.
    void BindTargets(RenderResources targets)
    {
        if (!TargetsMatch(targets))
        {
            RetireWaterSets();
            _bound = targets;
            _boundGen = targets.Generation;
            _boundUbo = _ubo;
            _boundMap = _ocean.Map;
            _boundBathy = _bathymetry.Texture;
        }
        _set ??= CreateWaterSet(targets, _bathymetry.NeutralTexture);
    }

    /// <summary>Resolve every submission and prepare its immutable resource bindings before recording.
    /// Failure leaves the frame unprepared. Unused sets retire so cached ownership stays bounded.
    /// <c>device.water.prepare</c> owns this device work on every viewport-prepared frame, with or without water.
    /// Device leaves cannot nest, so the bathymetry update here is charged to it directly.</summary>
    internal void PrepareOpticsFrame(RenderResources targets, in FramePrepare frame) =>
        PrepareOpticsFrame(targets, frame, frame.WaterPlanes);

    /// <summary>The scene form. <paramref name="absolute"/> is the absolute queue behind the render-relative
    /// <see cref="FramePrepare.WaterPlanes"/>, in the same order. Coverage revalidates from it exactly as
    /// <c>DrawWater</c> admitted it, never from separately reduced floats.</summary>
    internal void PrepareOpticsFrame(RenderResources targets, in FramePrepare frame, ReadOnlySpan<WaterPlane> absolute)
    {
        using var device = Profiling?.Cpu("device.water.prepare") ?? default;
        _opticsPrepared = false;
        _preparedBound = AnyBound(frame.WaterPlanes);
        RetryWaterOutputReplacement();
        ReadOnlySpan<WaterPlane> planes = frame.WaterPlanes;
        if (absolute.Length != planes.Length) throw new ArgumentException("The absolute queue must match the frame.", nameof(absolute));
        if (_bound is not null && !TargetsMatch(targets)) RetireWaterSets();
        _frameFields.Clear();
        for (int i = 0; i < planes.Length; i++)
        {
            WaterSettings effective = planes[i].Look?.ResolveInto(_effective, frame.Water) ?? frame.Water;
            if (!planes[i].Optics.IsBound) continue;
            WaterOpticsResources.Entry entry = ResolveOptics(planes[i].Optics.Field);
            WaterOpticsCoverage.Validate(absolute[i], effective, entry.Payload);
            _frameFields.Add(planes[i].Optics.Field);
        }
        _unusedFields.Clear();
        foreach (WaterOpticsHandle handle in _opticsSets.Keys)
            if (!_frameFields.Contains(handle)) _unusedFields.Add(handle);
        foreach (WaterOpticsHandle handle in _unusedFields)
        {
            _retired.Retire(_opticsSets[handle]);
            _opticsSets.Remove(handle);
        }
        if (planes.Length > 0)
        {
            // The sets below bind the depth field's texture, so its upload precedes them.
            _bathymetry.Update(frame.Water.Bathymetry);
            EnsureUboCapacity(planes.Length);
            BindTargets(targets);
            if (_preparedPlanes.Length < planes.Length)
            {
                int capacity = Math.Max(planes.Length, _preparedPlanes.Length * 2);
                _preparedPlanes = new PreparedPlane[capacity];
                _planeSets = new IGpuResourceSet[capacity];
            }
            for (int i = 0; i < planes.Length; i++)
            {
                WaterPlane plane = planes[i];
                WaterSettings effective = plane.Look?.ResolveInto(_effective, frame.Water) ?? frame.Water;
                int mode = plane.Optics.IsBound ? ResolveOptics(plane.Optics.Field).Payload.Mode : 0;
                PlaneRoute geometry = GeometryRoute(plane, frame.Water);
                PrepareWaterProgram(geometry, mode == 2);
                _preparedPlanes[i] = PreparedPlane.From(plane, effective, mode, geometry);
                IGpuResourceSet set = _set!;
                if (plane.Optics.IsBound && !_opticsSets.TryGetValue(plane.Optics.Field, out set!))
                {
                    set = CreateWaterSet(targets, Optics.Resolve(plane.Optics.Field).Texture);
                    _opticsSets.Add(plane.Optics.Field, set);
                }
                _planeSets[i] = set;
            }
        }
        if (planes.Length < _preparedPlaneCount)
            Array.Clear(_planeSets, planes.Length, _preparedPlaneCount - planes.Length);
        _preparedPlaneCount = planes.Length;
        _preparedBathymetry = frame.Water.Bathymetry;
        _preparedBathymetryRevision = frame.Water.Bathymetry?.Revision ?? 0;
        _preparedTime = frame.TimeSeconds;
        _preparedGridMode = frame.Water.GridMode;
        _preparedOcean = _preparedWantOcean ? OceanInputs.From(frame.Water.SeaState) : default;
        _opticsPrepared = true;
    }

    internal void VerifyOpticsFrame(RenderResources targets, ReadOnlySpan<WaterPlane> planes, WaterSettings settings, float time)
    {
        if (!OpticsFrameMatches(targets, planes, settings, time)) throw ChangedOpticsInputs();
    }

    // Whether recording still sees the inputs the prepared bindings were built from. Bound handles resolve here,
    // so a released field throws instead of comparing equal.
    bool OpticsFrameMatches(RenderResources targets, ReadOnlySpan<WaterPlane> planes, WaterSettings settings, float time)
    {
        if (!_opticsPrepared || _preparedPlaneCount != planes.Length || !TargetsMatch(targets)
            || !ReferenceEquals(_preparedBathymetry, settings.Bathymetry)
            || _preparedBathymetryRevision != (settings.Bathymetry?.Revision ?? 0) || _preparedTime != time
            || _preparedGridMode != settings.GridMode
            || (_preparedWantOcean && _preparedOcean != OceanInputs.From(settings.SeaState)))
            return false;
        for (int i = 0; i < planes.Length; i++)
        {
            WaterSettings effective = planes[i].Look?.ResolveInto(_effective, settings) ?? settings;
            int mode = planes[i].Optics.IsBound ? ResolveOptics(planes[i].Optics.Field).Payload.Mode : 0;
            if (_preparedPlanes[i] != PreparedPlane.From(planes[i], effective, mode, GeometryRoute(planes[i], settings)))
                return false;
        }
        return true;
    }

    static bool AnyBound(ReadOnlySpan<WaterPlane> planes)
    {
        for (int i = 0; i < planes.Length; i++)
            if (planes[i].Optics.IsBound) return true;
        return false;
    }

    static InvalidOperationException ChangedOpticsInputs() => new(
        "Bound water inputs changed or were not prepared. Queue all water and call Scene3D.PrepareFrame(width, height) before recording.");

    void PackOptics(ref WaterUbo u, in WaterPlane plane)
    {
        WaterOpticsBinding binding = plane.Optics;
        if (!binding.IsBound) return;
        WaterOpticsPayload payload = Optics.Resolve(binding.Field).Payload;
        u.OpticsRect = new(binding.Origin.X, binding.Origin.Z, 1f / binding.Size.X, 1f / binding.Size.Y);
        u.OpticsParams = new(binding.Origin.Y, payload.Mode, payload.Width, payload.Height);
    }
}
