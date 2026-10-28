using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Retained cutout shrubs, an analytic river and a two-cascade FFT ocean. Subclasses replace the water
/// planes and keep the foliage, the FFT settings and the point lights.</summary>
internal class RendererPerfOutdoorsScene : RendererPerfVillageScene
{
    readonly GltfMesh _shrub = FoliageShadowGpuScene.ShrubMesh();
    readonly RendererPerfTexture _cutout = new(8, 8, RendererPerfUiScene.ColorPixels(0), TextureMipPolicy.Full);
    readonly Scene3D.SurfaceMaps _surface = new(default, alphaCutoff: .5f);
    const float PatchSize = 4f;
    readonly FoliageInstance[] _instances;
    readonly FoliageRenderSettings _wind = new()
    {
        DrawRadius = 100f,
        DensityRadius = 100f,
        FadeBandWidth = 8f,
        InstanceFadeBandWidth = 1f,
        QualityDensity = 1f,
        DistantDensity = 1f,
        WindDirection = new(1f, .3f),
        WindStrength = .5f,
        WindSpeed = 1.8f,
        WindSpatialFrequency = .35f,
        WindFadeBladePixels = 0f,
    };
    readonly WaterPlane[] _water;
    readonly RendererPerfPoint[] _points =
    [
        new(new(-8f, 3f, -4f), Color.White, 32f, 3f, default),
        new(new(8f, 3f, 4f), Color.White, 32f, 3f, default),
    ];
    FoliageBatch? _batch;
    internal RendererPerfOutdoorsScene() : this("outdoors",
    [
        new(0f, .25f, -8f, 16f, 1.5f, new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = .08f }),
        new(0f, .25f, 16f, 16f, 8f),
    ])
    { }
    protected RendererPerfOutdoorsScene(string id, WaterPlane[] water) : base(id, 4, 0, 0)
    {
        _water = water;
        var motion = new FoliageMotionProfile(.3f, .15f, .4f, .035f);
        _instances = Enumerable.Range(0, 4096).Select(i =>
        {
            int patch = i / 64, local = i % 64;
            var position = new Vector3((patch % 8 - 4) * 4f + .25f + local % 8 * .45f,
                .1f, (patch / 8 - 4) * 4f + .25f + local / 8 * .45f);
            return new FoliageInstance(new MeshHandle(0), Matrix4x4.CreateTranslation(position),
                local / 64f, motion, true);
        }).ToArray();
    }
    protected override int Foliage => 4096;
    internal RendererPerfFoliageVisibility FoliageVisibility() => new(_instances, _shrub, PatchSize, _wind);
    protected override int Water => _water.Length;
    protected virtual RendererPerfClipmapSchedule ClipmapSchedule => new(new(33, 1, 1), new(224, 1, 1), 2, 2, 4);
    protected virtual string WaterRoute => "analytic-river-and-fft-ocean-clipmaps";
    protected override RendererPerfRecipeSchedule Schedule => base.Schedule with
    {
        DirectionalCache = "recomputed-displaced-foliage",
        Features = new()
        {
            FoliagePatches = 64,
            FoliageInstances = 4096,
            WaterPlanes = _water.Length,
            OceanDispatches = 2,
            OceanMipCopies = 4,
            OceanPrime = "first-warmup-only",
            Clipmap = ClipmapSchedule
        },
    };
    protected override object Geometry => new
    {
        Rigid = base.Geometry,
        Shrub = MeshFacts(_shrub),
        Instances = _instances,
        PatchSize,
        Surface = _surface,
        ClipmapStates = BuildClipmapStates(),
        Buffers = new
        {
            FoliageStride = ModelRenderer.FoliageInstanceData.SizeInBytes,
            FoliageBytes = 4096 * ModelRenderer.FoliageInstanceData.SizeInBytes,
            FoliageUniformStride = ModelRenderer.FoliageUniforms.SizeInBytes,
            WaterVertexStride = WaterRenderer.ClipVertexBytes,
            WaterVerticesPerPlane = WaterClipmap.VertexCount(4, 32),
            WaterIndicesPerPlane = WaterClipmap.IndexCount(4, 32)
        },
        Wind = _wind,
        Interactors = new FoliageInteractor[0],
        Focus = "indexed-camera-target",
        WaterPlanes = _water,
        Points = _points,
        Routes = new
        {
            NativePatches = 64,
            InstancesPerPatch = 64,
            CutoutShadows = true,
            Water = WaterRoute,
            OceanDispatches = 2,
            OceanMipCopies = 4,
            OceanPrime = "first-warmup-only",
            OceanSteadyStalls = 0,
            Directional = "four-cascades-recomputed",
            Temporal = "native-history-after-first-frame",
        },
    };
    protected override object Assets => new
    {
        Terrain = base.Assets,
        Cutout = _cutout,
    };
    protected override void ConfigureSettings(PixelPostProcessSettings post)
    {
        OceanFocusScene.BaseSea(post.Water.SeaState);
        post.Water.SeaState.CascadeResolution = 256;
        post.Water.WaveSource = WaterWaveSource.FftOcean;
        post.Water.GridMode = WaterGridMode.Clipmap;
        post.Water.ClipmapCellSize = .5f;
        post.Water.ClipmapRingCells = 32;
        post.Water.ClipmapLevels = 4;
    }
    // Per sequence index and plane, the snapped clipmap indices the camera focus selects.
    protected long[][][] BuildClipmapStates()
    {
        WaterSettings settings = Settings().Water;
        return Enumerable.Range(0, Period).Select(index =>
        {
            RendererPerfRecipeFrame frame = Frame(index);
            IsoCamera3D camera = Camera();
            camera.Target = frame.Target;
            camera.Azimuth = frame.Azimuth;
            return _water.Select(plane =>
            {
                Vector2 focus = WaterClipmap.ClampFocus(plane, camera.Eye.X, camera.Eye.Z);
                var snaps = new long[settings.ClipmapLevels * 2];
                WaterClipmap.SnapIndices(focus.X, focus.Y, settings.ClipmapCellSize, settings.ClipmapLevels,
                    snaps.AsSpan(0, settings.ClipmapLevels), snaps.AsSpan(settings.ClipmapLevels));
                return snaps;
            }).ToArray();
        }).ToArray();
    }
    protected override IsoCamera3D Camera()
    {
        IsoCamera3D camera = base.Camera();
        camera.Distance = 32f;
        return camera;
    }
    protected override void Load(Scene3D scene)
    {
        base.Load(scene);
        var texture = _cutout.Load(scene);
        MeshHandle mesh = scene.LoadMesh(_shrub, new Scene3D.SurfaceMaps(texture, _surface.Normal, _surface.Roughness, _surface.AlphaCutoff));
        _batch = scene.CreateFoliageBatch(_instances.Select(i => i with { Mesh = mesh }).ToArray(), PatchSize);
    }
    internal override void PrepareCold(Scene3D scene, IGpuDevice device, int firstFrameIndex)
    {
        // Prepare and record only ocean compute. Do not consume Begin, pose or foliage history indices.
        scene.EffectTimeSeconds = SequenceIndex(firstFrameIndex) * Dt;
        foreach (WaterPlane plane in _water) scene.DrawWater(plane);
        scene.PrepareFrame();
        RendererPerfColdOcean.Record(scene, device);
    }
    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        base.QueueContent(scene, frameIndex);
        scene.DrawFoliage(_batch!, Frame(frameIndex).Target, _wind);
        foreach (WaterPlane plane in _water) scene.DrawWater(plane);
        foreach (RendererPerfPoint point in _points) scene.AddLight(point.Position, point.Color, point.Radius, point.Intensity, point.Shadow);
    }
    // LastWaterStats deliberately retains the last water draw. Inactive frames are unavailable, never stale or zero.
    internal static WaterFrameStats? CurrentWaterStats(Scene3D scene) => scene.WaterPlaneCount > 0 ? scene.LastWaterStats : null;
}
