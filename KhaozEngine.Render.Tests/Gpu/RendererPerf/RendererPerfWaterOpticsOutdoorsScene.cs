using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The v1 outdoors foliage, FFT settings and point lights, with its two planes replaced by one FFT water
/// optics pool beyond the terrain's +z edge.</summary>
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

    internal RendererPerfWaterOpticsOutdoorsScene(RendererPerfWaterOpticsProfile profile) : this(profile, CreatePool()) { }

    RendererPerfWaterOpticsOutdoorsScene(RendererPerfWaterOpticsProfile profile, RendererPerfWaterOpticsPool pool)
        : base(RendererPerfWaterOptics.RecipeId("outdoors", profile), [pool.Plane])
    {
        Profile = profile;
        Pool = pool;
        Pool.Place(Camera().ViewProjection);
    }

    public RendererPerfWaterOpticsProfile Profile { get; }
    public RendererPerfWaterOpticsPool Pool { get; }
    public WaterSettings WaterSettings => Settings().Water;
    protected override string WaterRoute => "fft-ocean-pool-clipmap";
    protected override RendererPerfClipmapSchedule ClipmapSchedule => RendererPerfClipmapEvents.Derive(BuildClipmapStates());
    protected override object Geometry => new { Rigid = base.Geometry, Pool = Pool.Geometry };
    protected override object ExpectedCounts => RendererPerfWaterOpticsExpected.Create(
        [.. RigidBounds().Where(draw => draw.Ground)], [.. RigidBounds().Concat(Pool.RigidBounds())],
        FoliageVisibility(), Buildings, Props, Foliage, Water,
        [.. MeasuredIndices.Select(index => (Frame(index).Target, Frame(index).ViewProjection))]);

    static RendererPerfWaterOpticsPool CreatePool()
    {
        var water = new WaterSettings();
        ConfigureSea(water.SeaState);
        // The terrain spans -16 to 16 on x and z. The FFT plane keeps the scene's sea state, so no look.
        return new RendererPerfWaterOpticsPool(-16f, 16f, 16f, 48f, RendererPerfWaterOpticsPool.FftDisplacementBound(water),
            null, Seed, PropScale);
    }

    static void ConfigureSea(WaterSeaState sea)
    {
        OceanFocusScene.BaseSea(sea);
        sea.CascadeResolution = 256;
        sea.FetchKilometres = FetchKilometres;
    }

    protected override void ConfigureSettings(PixelPostProcessSettings post)
    {
        base.ConfigureSettings(post);
        ConfigureSea(post.Water.SeaState);
        RendererPerfWaterOptics.Apply(post.Water, Profile);
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
