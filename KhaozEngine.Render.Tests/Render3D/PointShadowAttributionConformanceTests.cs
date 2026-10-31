using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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
                    var request = recorder.NextShadowStageRequest;
                    f.Render(ShadowStageProbeMode.Counts, foliage: foliage, probeRequest: request);
                    recorder.Observe(.016, 1000 + i * 16, window, default, f.Frame.CompletedShadowStageCallback);
                }
            }
            await recorder.Publication;
            Assert.Null(recorder.Failure);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(6, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("pointShadowAttribution").GetProperty("frames").GetArrayLength());
            if (Environment.GetEnvironmentVariable("KE_POINT_ATTRIBUTION_TEST_EXPORT") is { Length: > 0 } export)
                File.Copy(path, export, overwrite: false);
        }
        finally { Directory.Delete(directory, true); }
    }
}
