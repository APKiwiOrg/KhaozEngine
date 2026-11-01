using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointShadowAttributionConformanceTests
{
    [Fact]
    public async Task Actual_recorder_serializes_owned_renderer_tables_after_scene_disposal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-point-conformance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "report.json");
            var window = new FrameDiagnosticsWindowState(true, false, false, 32, 24, 0, false);
            using var recorder = new FrameDiagnosticsRecorder(path, 1000, 2, ShadowStageProbeMode.Counts, pointAttribution: true);
            using (var f = new ShadowStageProbeFixture())
            {
                using var foliage = f.Foliage();
                f.Render(ShadowStageProbeMode.Off, foliage: foliage);
                f.Render(ShadowStageProbeMode.Off, foliage: foliage);
                File.WriteAllText(path + ".start", "");
                recorder.Observe(.016, 0, window, default); recorder.Observe(.016, 1000, window, default);
                for (int i = 1; i <= 2; i++)
                {
                    AppWindow.BeginShadowStageCallback(f.Frame, recorder);
                    Assert.Same(recorder.NextPointAttribution, f.Frame.ShadowStagePointAttribution);
                    GameApp3D.PrepareProbedScene(f.Scene, scene =>
                    {
                        scene.Draw(new RigidInstanceDraw(f.Mesh, Matrix4x4.CreateTranslation(1.5f, .5f, 0)) { Motion = MotionKey.From(71) });
                        scene.DrawFoliage(foliage, Vector3.Zero, new FoliageRenderSettings { DrawRadius = 100, DistantDensity = 1 });
                        scene.AddLight(new(0, 2, 0), Color.White, 6, 1, LightShadow.Static(1));
                        scene.AddLight(new(0, 2, 0), Color.White, 6, 1, LightShadow.Dynamic);
                    }, f.Frame);
                    f.Commands.Clear(); f.Commands.CommandTrace!.Clear();
                    f.Commands.Begin(); f.Scene.RenderInternal(f.Commands, 32, 24, f.Target); f.Commands.End();
                    f.Scene.PublishShadowStageProbe(f.Frame); f.Frame.CompleteShadowStageCallback();
                    recorder.Observe(.016, 1000 + i * 16, window, default, f.Frame.CompletedShadowStageCallback,
                        completedPointAttribution: f.Frame.ShadowStagePointAttribution);
                }
            }
            await recorder.Publication;
            Assert.Null(recorder.Failure);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("pointShadowAttribution").GetProperty("frames").GetArrayLength());
            Assert.Equal(2, json.RootElement.GetProperty("shadowStages").GetProperty("validRows").GetInt32());
            foreach (var sample in json.RootElement.GetProperty("samples").EnumerateArray())
            {
                var stage = sample.GetProperty("shadowStage");
                Assert.Equal((int)ShadowStageProbeStatus.Valid, stage.GetProperty("status").GetInt32());
                Assert.Equal(3, stage.GetProperty("request").EnumerateObject().Count());
            }
            if (Environment.GetEnvironmentVariable("KE_POINT_ATTRIBUTION_TEST_EXPORT") is { Length: > 0 } export)
                File.Copy(path, export, overwrite: false);
        }
        finally { Directory.Delete(directory, true); }
    }
}
