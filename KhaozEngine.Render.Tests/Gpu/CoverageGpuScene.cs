using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal readonly record struct CoverageCase(int View)
{
    internal bool Shadows { get; init; }
    internal bool StaticLight { get; init; }
    internal bool ShadowOnly { get; init; }
    internal bool Outlines { get; init; }
    internal float EquipmentFade { get; init; }
    internal int CameraMode { get; init; }
    internal float PointRadius { get; init; }
    internal bool ExpectRefusal { get; init; }
    internal bool CameraCulled { get; init; }
}

internal sealed record CoverageCapture(byte[] Colour, CoverageTargetReadback Depth, CoverageTargetReadback Normal,
    CoverageTargetReadback? Cascades, CoverageTargetReadback? Points, CoverageTargetReadback? TransientPoints,
    CoverageTargetReadback? FullOutline, CoverageTargetReadback? VisibleOutline,
    string SubmittedSurfaceSignature, GpuBackendKind Backend, string Device, string Artifact)
{
    internal MotionTargetReadback? Motion { get; init; }
    internal Vector2 Jitter { get; init; }
    internal CoverageTargetReadback? ReconstructionColour { get; init; }
    internal RenderFrameStats FrameStats { get; init; }
    internal TemporalDiagnostics Temporal { get; init; }
}

public sealed partial class CoverageGpuScene : IDisposable
{
    internal const int Width = 320, Height = 240;
    TemporalFixture? fixture;
    CoverageGpuActor[]? actors;
    MeshHandle occluder;
    readonly string captureId = Guid.NewGuid().ToString("N");
    int captureNumber;
    internal TemporalFixture Fixture => fixture ??= Create();
    internal CoverageGpuActor[] Actors { get { _ = Fixture; return actors!; } }

    TemporalFixture Create()
    {
        var result = new TemporalFixture(Width, Height, Configure);
        actors = new CoverageGpuActor[14];
        for (int i = 0; i < actors.Length; i++) actors[i] = new(result.Scene, i, mapped: i is 2 or 3);
        occluder = result.Scene.LoadMesh(MeshPrimitives.Box(.6f));
        return result;
    }

    static void Configure(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Sky.Enabled = false;
        scene.Post.Atmosphere.Enabled = false;
        scene.Post.Outline = false;
        scene.Post.Hdr.Enabled = false;
        scene.Post.Bloom.Enabled = false;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Post.BackgroundColor = new(.02f, .15f, .035f, 1);
        scene.Post.AmbientColor = new(.5f, .5f, .5f, 1);
        scene.Camera.Distance = 6;
        scene.Camera.NearPlane = .1f;
        scene.Camera.FarPlane = 40;
    }

    internal CoverageCapture Capture(CoverageCase testCase, bool coverage, bool gpuSkinning) =>
        CaptureRoute(testCase, coverage ? 1 : 0, gpuSkinning);
    internal CoverageCapture CaptureReference(CoverageCase testCase, bool gpuSkinning) =>
        CaptureRoute(testCase, 2, gpuSkinning);
    internal CoverageCapture CaptureRemoval(CoverageCase testCase) => CaptureRoute(testCase, 3, false);

    CoverageCapture CaptureRoute(CoverageCase row, int route, bool gpuSkinning)
    {
        Scene3D scene = Fixture.Scene;
        SetCase(row, gpuSkinning);
        if (row.Shadows) PrimePoints(row);
        var submitted = new List<CoverageGpuActor>();
        byte[] colour = Fixture.Frame((s, _) =>
        {
            if (row.Shadows)
            {
                s.AddLight(Vector3.Zero, Color.White, row.PointRadius > 0 ? row.PointRadius : 6, 1,
                    row.StaticLight ? LightShadow.Static(8001) : LightShadow.Dynamic);
                foreach (var (actor, world) in ShadowActors())
                {
                    actor.Draw(row, route, world);
                    submitted.Add(actor);
                }
            }
            else
            {
                int first = row.View % 2 == 0 ? 0 : 2;
                for (int i = 0; i < 2; i++)
                {
                    var actor = Actors[first + i];
                    actor.Draw(row, route, Matrix4x4.CreateTranslation(i * 3, 0, 0));
                    submitted.Add(actor);
                }
                if (row.Outlines) s.Draw(occluder, Matrix4x4.CreateTranslation(3.1f, 0, .6f));
            }
        });
        if (route == 1 && !row.ExpectRefusal)
        {
            foreach (var actor in submitted) actor.AssertCovered();
            if (row.Shadows)
            {
                var pointMatrices = scene.CoveragePointMatricesForTests.ToArray();
                for (int i = 0; i < submitted.Count; i++)
                {
                    var passes = scene.CoveragePassesForTests(submitted[i].Frame).ToArray();
                    int hostFaces = passes.Count(p => p.CommandId == 0 && pointMatrices.Contains(p.ViewProjection));
                    if (row.ShadowOnly || row.CameraCulled && i is 2 or 3) Assert.False(scene.CoverageMainVisibleForTests(submitted[i].Frame));
                    if (i < 6) Assert.True(hostFaces >= 6, $"Missing nearby point host faces: {i} count={hostFaces}");
                    if (i >= 8) Assert.DoesNotContain(passes, p => pointMatrices.Contains(p.ViewProjection));
                }
            }
        }
        if (row.ExpectRefusal)
            Assert.Contains(submitted, actor => scene.GetCoverageOutcome(actor.Frame).Presentation == CoveragePresentation.UncoveredFallback);
        var diagnostics = scene.CoverageDiagnosticsForTests;
        if (!row.ExpectRefusal) Assert.Equal(route == 1 ? submitted.Count : 0, diagnostics.CoveredCpuPreparations);
        if (route == 0) Assert.Equal(gpuSkinning ? submitted.Count : 0, diagnostics.UncoveredGpuDraws);
        var outlines = row.Outlines ? scene.ReadCoverageOutlinesForTests() : default;
        var capture = new CoverageCapture(colour, scene.ReadCoverageDepthForTests(), scene.ReadCoverageNormalForTests(),
            row.Shadows ? scene.ReadCoverageCascadesForTests() : null,
            row.Shadows ? scene.ReadCoveragePointsForTests(false) : null,
            row.Shadows ? scene.ReadCoveragePointsForTests(true) : null,
            outlines.Full, outlines.Visible, submitted[0].Signature, Fixture.Device.Backend,
            Fixture.Device.Capabilities.DeviceName, $"coverage-{captureId}-{captureNumber++}-camera{row.CameraMode}-v{row.View}-s{row.Shadows}-static{row.StaticLight}-only{row.ShadowOnly}-culled{row.CameraCulled}-o{row.Outlines}-gpu{gpuSkinning}-route{route}")
        { ReconstructionColour = scene.ReadCoverageReconstructionColourForTests(), FrameStats = scene.LastFrameStats };
        CoverageArtifacts.Write(capture);
        return capture;
    }

    void PrimePoints(CoverageCase row)
    {
        void Frame() => Fixture.Frame((s, _) =>
        {
            s.AddLight(Vector3.Zero, Color.White, row.PointRadius > 0 ? row.PointRadius : 6, 1,
                row.StaticLight ? LightShadow.Static(8001) : LightShadow.Dynamic);
            foreach (var (actor, world) in ShadowActors()) actor.Draw(row, 0, world);
        });
        // Request, base residency and transient demand are three distinct boundary stages.
        Frame();
        Frame();
        Frame();
    }

    void SetCase(CoverageCase row, bool gpuSkinning)
    {
        var scene = Fixture.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        scene.FrustumCulling = true;
        scene.CameraOverride = null;
        scene.RenderOrigin = row.View == 4 ? new Vector3(128, 0, 0) : Vector3.Zero;
        scene.Post.CelBands = row.View % 2 == 0 ? 3 : 0;
        scene.Post.LightDirection = row.View == 5 ? Vector3.UnitZ : new(.3f, -.4f, -1);
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        scene.Post.Quality.Shadows.Mode = row.Shadows ? ShadowMode.ShadowMap : ShadowMode.Off;
        scene.RequestShadowMapLayout(256, 4);
        scene.Post.Quality.Shadows.ShadowMaxDistance = 30;
        scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 1;
        scene.Camera.Target = row.Shadows ? Vector3.Zero : new(1.5f, 0, 0);
        scene.Camera.OrthoSize = row.Shadows ? row.CameraCulled ? 3 : 5 : 4;
        scene.Camera.Azimuth = row.View switch { 1 => MathF.PI, 2 => 1.3f, 3 => -1.3f, _ => 0 };
        scene.Camera.Elevation = row.View switch { 4 => 1.1f, 5 => -1.1f, _ => 0 };
        if (row.CameraMode != 0) scene.CameraOverride = new CoverageGpuCamera(scene.Camera, row.CameraMode == 2);
    }

    IEnumerable<(CoverageGpuActor Actor, Matrix4x4 World)> ShadowActors()
    {
        Matrix4x4[] worlds =
        [
            Matrix4x4.CreateRotationY(-MathF.PI / 2) * Matrix4x4.CreateTranslation(4, 0, 0),
            Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(-4, 0, 0),
            Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 4, 0),
            Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateTranslation(0, -4, 0),
            Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0, 0, 4),
            Matrix4x4.CreateTranslation(0, 0, -4),
            Matrix4x4.CreateTranslation(0, 0, 2),
            Matrix4x4.CreateTranslation(0, 0, -2),
            Matrix4x4.CreateTranslation(0, 0, -9),
            Matrix4x4.CreateTranslation(0, 0, -19),
        ];
        for (int i = 0; i < worlds.Length; i++) yield return (Actors[i + 4], worlds[i]);
    }

    public void Dispose()
    {
        if (actors is not null) foreach (var actor in actors) actor.Dispose();
        fixture?.Dispose();
        fixture = null;
        actors = null;
    }
}
