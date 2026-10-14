using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Phase-correct downward fog and the near onset on the GPU, toward a rising sun from an elevated
/// camera (KhaozEngine #1406).</summary>
[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereFogPhaseGpuTests : IClassFixture<AtmosphereGoldenScene>
{
    const float SunriseTime = .268f;
    const float CameraY = 16;
    readonly AtmosphereGoldenScene _fixture;

    public AtmosphereFogPhaseGpuTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    // Moderate haze keeps the patch's transmittance mid-range, so the 80 m onset visibly clears it at every pitch.
    const float OnsetHazePerKm = 1;
    const float OnsetDistance = 80;

    [GpuFact]
    public void DownwardFogTowardTheSun_MatchesCpuFog()
    {
        foreach (float pitchDegrees in new[] { -15f, -35f, -60f })
        {
            var (gpuLinear, cpuLinear) = Downward(pitchDegrees, 0);
            var (gpuOnset, cpuOnset) = Downward(pitchDegrees, OnsetDistance);
            Vector3 gpuDelta = gpuOnset - gpuLinear, cpuDelta = cpuOnset - cpuLinear;
            float cpuChange = MaxChannel(Vector3.Abs(cpuDelta));
            float deltaError = MaxChannel(Vector3.Abs(gpuDelta - cpuDelta));
            Console.WriteLine(
                $"FOG_ONSET_GPU pitch={pitchDegrees} gpuDelta={gpuDelta} cpuDelta={cpuDelta} error={deltaError}");
            Assert.True(cpuChange >= .03f, $"Onset barely changes the CPU fog at pitch {pitchDegrees}: {cpuDelta}.");
            Assert.True(deltaError <= .01f,
                $"Onset delta at pitch {pitchDegrees}: GPU {gpuDelta}, CPU {cpuDelta}, error {deltaError}.");
            foreach (var (onset, gpu, cpu) in new[] { (0f, gpuLinear, cpuLinear), (OnsetDistance, gpuOnset, cpuOnset) })
                Assert.True(MaxChannel(Vector3.Abs(gpu - cpu)) <= .03f,
                    $"Downward fog at pitch {pitchDegrees} and onset {onset}: GPU {gpu}, CPU {cpu}.");
        }
    }

    // Display-space GPU patch and tonemapped CPU fog for a black floor under the image centre.
    (Vector3 Gpu, Vector3 Cpu) Downward(float pitchDegrees, float onset)
    {
        Vector3 forward = default;
        byte[] pixels = _fixture.CaptureEffects(scene =>
        {
            forward = Sunrise(scene, pitchDegrees);
            scene.Post.Atmosphere.HazeDensityPerKm = OnsetHazePerKm;
            scene.Post.Atmosphere.AerialOnsetDistance = onset;
        }, Ground);
        var post = _fixture.Scene.Post;
        var model = post.ResolveAtmosphereModel();
        var frame = model.CreateFrame(post.AtmosphereCycle!.Value);
        var eye = new Vector3(0, CameraY, 0);
        var fragment = eye + forward * (CameraY / -forward.Y);
        var expected = AtmosphereFog.Shade(post.Atmosphere, model, frame, Vector3.Zero, eye, fragment, Vector3.Zero);
        var mapped = TonemapMath.Map(expected.X, expected.Y, expected.Z, post.Hdr.Exposure, (int)post.Hdr.Operator,
            post.Hdr.ChromaPreservation);
        var actual = Patch(pixels, AtmosphereGoldenScene.Width / 2, AtmosphereGoldenScene.Height / 2);
        var cpu = new Vector3(mapped.R, mapped.G, mapped.B);
        Console.WriteLine($"FOG_PHASE_GPU pitch={pitchDegrees} onset={onset} gpu={actual} cpu={cpu}");
        return (actual, cpu);
    }

    static float MaxChannel(Vector3 value) => MathF.Max(value.X, MathF.Max(value.Y, value.Z));

    [GpuFact]
    public void Golden_AtmosphereSunriseOverTreeLine()
    {
        byte[] rgba = _fixture.CaptureEffects(scene =>
        {
            Sunrise(scene, -12);
            ((FlyCamera3D)scene.CameraOverride!).FarPlane = 25000;
            scene.Post.Atmosphere.HazeDensityPerKm = .3f;
            scene.Post.Atmosphere.AerialOnsetDistance = 80;
        }, TreeLine);
        AtmosphereGoldenScene.WriteCapture("scene3d_atmosphere_sunrise", rgba);
        GoldenCompare.AssertOrUpdate("scene3d_atmosphere_sunrise", rgba, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
    }

    // Faces the rising sun's azimuth from 16 m and returns the unit view direction at the image centre.
    Vector3 Sunrise(Scene3D scene, float pitchDegrees)
    {
        _fixture.TimeOfDay = SunriseTime;
        var sun = -SunCycle.Evaluate(SunriseTime, _fixture.Cycle).LightDirection;
        float yaw = MathF.Atan2(sun.X, sun.Z), pitch = pitchDegrees * MathF.PI / 180;
        var camera = (FlyCamera3D)scene.CameraOverride!;
        camera.Position = new Vector3(0, CameraY, 0);
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.FarPlane = 10000;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        return new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
    }

    static void Ground(Scene3D scene)
    {
        var ground = scene.GetOrLoadMesh("atmosphere-phase-ground", () => scene.LoadMesh(MeshPrimitives.Tile(6000)));
        scene.Draw(ground, Matrix4x4.CreateTranslation(0, -.1f, 0), Color.Black);
    }

    // Grass and a staggered row of dark trunks and crowns 40 to 160 m toward the sun, all below the horizon.
    void TreeLine(Scene3D scene)
    {
        // Wide enough that the far-plane cut sits above the geometric horizon, so no planet-ground sky shows.
        var ground = scene.GetOrLoadMesh("atmosphere-phase-wide-ground", () => scene.LoadMesh(MeshPrimitives.Tile(60000)));
        var box = scene.GetOrLoadMesh("atmosphere-phase-box", () => scene.LoadMesh(MeshPrimitives.Box()));
        scene.Draw(ground, Matrix4x4.CreateTranslation(0, -.1f, 0), new Color(.22f, .36f, .16f));
        var sun = -SunCycle.Evaluate(SunriseTime, _fixture.Cycle).LightDirection;
        var forward = Vector3.Normalize(new Vector3(sun.X, 0, sun.Z));
        var right = new Vector3(forward.Z, 0, -forward.X);
        for (int i = -7; i <= 7; i++)
        {
            float distance = 40 + 30 * ((i + 7) % 5);
            float height = 8 + 2 * ((i + 7) % 3);
            var foot = forward * distance + right * (i * 9f);
            scene.Draw(box, Matrix4x4.CreateScale(1, height, 1) * Matrix4x4.CreateTranslation(foot + new Vector3(0, height / 2, 0)),
                new Color(.18f, .12f, .08f));
            scene.Draw(box, Matrix4x4.CreateScale(6, 5, 6) * Matrix4x4.CreateTranslation(foot + new Vector3(0, height, 0)),
                new Color(.1f, .24f, .1f));
        }
    }

    static Vector3 Patch(byte[] rgba, int x, int y)
    {
        Vector3 sum = default;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int i = ((y + dy) * AtmosphereGoldenScene.Width + x + dx) * 4;
                sum += new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f;
            }
        return sum / 9;
    }
}
