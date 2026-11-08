using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The v1 outdoors foliage and point lights with one water optics pool. V2 uses FFT, while v3 uses
/// the village procedural swell so underwater caustics can participate.</summary>
internal sealed class RendererPerfWaterOpticsOutdoorsScene : RendererPerfOutdoorsScene, IRendererPerfWaterOpticsScene
{
    // Clearance needs a guaranteed bound on the surface height (RendererPerfWaterOpticsPool.FftDisplacementBound),
    // not a statistic. The v1 sea state's bound exceeds 30 m, and even a 0.25 km fetch leaves 4.85 m, so no
    // submerged caster fits a 1.5 m column. A 20 m fetch, shorter than this 32 m pool, keeps the v1 wind,
    // direction, cascades and 256-point kernels and bounds the surface at 0.0955 m, close to the village swell.
    // v1 outdoors keeps its sea state.
    const float FetchKilometres = .02f;
    // Frames the pool so its surface covers about half of every measured frame.
    const float OrthoSize = 17f;
    // This pool is half the village pool's width and its closer frame shows only the near half of it, where
    // the water is shallow. Half-size props are the largest that fit all 48 in the deep enough water in frame.
    const float PropScale = .5f;

    readonly bool _v3;

    internal RendererPerfWaterOpticsOutdoorsScene(RendererPerfWaterOpticsProfile profile, string campaign = "water-optics-v2")
        : this(profile, campaign, CreatePool(campaign)) { }

    RendererPerfWaterOpticsOutdoorsScene(RendererPerfWaterOpticsProfile profile, string campaign, RendererPerfWaterOpticsPool pool)
        : base(RendererPerfWaterOptics.RecipeId("outdoors", profile, campaign), [pool.Plane])
    {
        _v3 = campaign == "water-optics-v3";
        Profile = profile;
        Pool = pool;
        Pool.Place(Camera().ViewProjection);
    }

    public RendererPerfWaterOpticsProfile Profile { get; }
    public RendererPerfWaterOpticsPool Pool { get; }
    public WaterSettings WaterSettings => Settings().Water;
    protected override string WaterRoute => _v3 ? "procedural-pool-clipmap" : "fft-ocean-pool-clipmap";
    protected override RendererPerfClipmapSchedule ClipmapSchedule => RendererPerfClipmapEvents.Derive(BuildClipmapStates());
    protected override object Geometry => new { Rigid = base.Geometry, Pool = Pool.Geometry };
    protected override object ExpectedCounts => RendererPerfWaterOpticsExpected.Create(
        [.. RigidBounds().Where(draw => draw.Ground)], [.. RigidBounds().Concat(Pool.RigidBounds())],
        FoliageVisibility(), Buildings, Props, Foliage, Water,
        [.. MeasuredIndices.Select(index => (Frame(index).Target, Frame(index).ViewProjection))]);

    protected override bool HasFftWater => !_v3;

    static RendererPerfWaterOpticsPool CreatePool(string campaign)
    {
        var water = new WaterSettings();
        ConfigureSea(water.SeaState);
        float fftBound = RendererPerfWaterOpticsPool.FftDisplacementBound(water);
        if (campaign == "water-optics-v3")
            return new RendererPerfWaterOpticsPool(-16f, 16f, 16f, 48f, .08f,
                new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = .08f },
                Seed, PropScale, propWaveBound: fftBound);
        // The terrain spans -16 to 16 on x and z. The FFT plane keeps the scene's sea state, so no look.
        return new RendererPerfWaterOpticsPool(-16f, 16f, 16f, 48f, fftBound,
            null, Seed, PropScale);
    }

    static void ConfigureSea(WaterSeaState sea)
    {
        RendererPerfSceneInputs.BaseSea(sea);
        sea.CascadeResolution = 256;
        sea.FetchKilometres = FetchKilometres;
    }

    protected override void ConfigureSettings(PixelPostProcessSettings post)
    {
        base.ConfigureSettings(post);
        ConfigureSea(post.Water.SeaState);
        RendererPerfWaterOptics.Apply(post.Water,
            Profile == RendererPerfWaterOpticsProfile.M3On ? RendererPerfWaterOpticsProfile.M5On : Profile);
        if (_v3)
        {
            post.Water.UnderwaterCaustics = Profile == RendererPerfWaterOpticsProfile.M3On;
            // The owner's River pick. Keep the legacy profiles' default strength frozen.
            if (post.Water.UnderwaterCaustics) post.Water.CausticStrength = 4f;
        }
    }

    protected override IsoCamera3D Camera()
    {
        IsoCamera3D camera = base.Camera();
        camera.Target = new Vector3((Pool.MinX + Pool.MaxX) * .5f, 0f, Pool.ShoreZ);
        camera.OrthoSize = OrthoSize;
        return camera;
    }

    protected override void Load(Scene3D scene)
    {
        base.Load(scene);
        Pool.Load(scene);
    }

    // The base queues the pool plane with the scene's water, so only the bed and props are added here.
    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        base.QueueContent(scene, frameIndex);
        Pool.QueueProps(scene);
    }
}
