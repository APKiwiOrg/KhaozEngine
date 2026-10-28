using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The v1 village with a procedural water optics pool beyond its +z edge.</summary>
internal sealed class RendererPerfWaterOpticsVillageScene : RendererPerfVillageScene, IRendererPerfWaterOpticsScene
{
    // The village terrain spans -32 to 32 on x and z.
    const float SwellAmplitude = .08f;
    // Frames the pool so its surface covers about half of every measured frame.
    const float OrthoSize = 30f;

    internal RendererPerfWaterOpticsVillageScene(RendererPerfWaterOpticsProfile profile)
        : base(RendererPerfWaterOptics.RecipeId("village", profile), 8, 1024, 24)
    {
        Profile = profile;
        Pool = new RendererPerfWaterOpticsPool(-32f, 32f, 32f, 64f, SwellAmplitude,
            new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = SwellAmplitude }, Seed);
        Pool.Place(Camera().ViewProjection);
    }

    public RendererPerfWaterOpticsProfile Profile { get; }
    public RendererPerfWaterOpticsPool Pool { get; }
    public WaterSettings WaterSettings => Settings().Water;
    protected override int Water => 1;
    protected override RendererPerfRecipeSchedule Schedule => base.Schedule with { Features = new() { WaterPlanes = 1 } };
    protected override object Geometry => new { Rigid = base.Geometry, Pool = Pool.Geometry };
    protected override object ExpectedCounts => RendererPerfWaterOpticsExpected.Create(
        [.. RigidBounds().Where(draw => draw.Ground)], [.. RigidBounds().Concat(Pool.RigidBounds())],
        Buildings, Props, Foliage, Water, [.. MeasuredIndices.Select(index => Frame(index).ViewProjection)]);

    protected override void ConfigureSettings(PixelPostProcessSettings post)
    {
        base.ConfigureSettings(post);
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

    protected override void QueueContent(Scene3D scene, int frameIndex)
    {
        base.QueueContent(scene, frameIndex);
        Pool.Queue(scene);
    }
}
