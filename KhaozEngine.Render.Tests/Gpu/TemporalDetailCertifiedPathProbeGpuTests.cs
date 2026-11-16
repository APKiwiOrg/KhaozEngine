using System;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Report-only: where the certified and checked detail resolves first store different fine bytes on the running
/// device, read through the test-only path output. The plain row reads the chosen UV, retention and path flag. The
/// inputs row replaces the UV with two of the retention's inputs, the current colour's red and the current weight. Both
/// rows add an output, so a compiler may treat the programs differently from production. The asserting comparison is
/// <see cref="TemporalVisibilityFullyLitGpuTests.CertifiedScenesMatchTheCheckAndBothEntriesOnEveryHistoryByte"/>.</summary>
[Trait("Category", "Diagnostic")]
public sealed class TemporalDetailCertifiedPathProbeGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
{
    const string RetentionLine =
        "        detailTransport.z = detailRetention(storedR.rgb, currentColor, detailMean.rgb, currentWeight, useHistory);\n";

    [GpuTheory]
    [InlineData(TemporalUpscale.Native, false)]
    [InlineData(TemporalUpscale.Native, true)]
    [InlineData(TemporalUpscale.Quality, false)]
    [InlineData(TemporalUpscale.Quality, true)]
    public void Certified_and_checked_detail_paths_report_their_first_divergence(TemporalUpscale preset, bool inputs)
    {
        string Probe(string text) => inputs
            ? text.Replace(RetentionLine, RetentionLine + "        detailTransport.xy = vec2(currentColor.r, currentWeight);\n",
                StringComparison.Ordinal)
            : text;
        string Checked(string text)
        {
            foreach (TemporalResolvePrecision precision in new[] { TemporalResolvePrecision.Full, TemporalResolvePrecision.Half })
            {
                string capture = ShaderSources.TemporalDetailResolveFragment(precision, capturePathForTests: true);
                if (text == TemporalVisibilityPrograms.WithoutDecision(capture)) return Probe(capture);
            }
            return Probe(TemporalVisibilityFullyLitGpuTests.ForceCheck(text));
        }
        var stages = new FrontStage[2];
        var fixtures = new TemporalFixture[2];
        var draws = new Action<Scene3D, int>[2];
        try
        {
            for (int i = 0; i < fixtures.Length; i++)
            {
                var stage = stages[i] = new FrontStage(320, 180, 4.5f);
                var fx = fixtures[i] = new TemporalFixture(320, 180, s =>
                {
                    stage.Setup(s, AntiAliasing.Temporal, preset);
                    s.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
                    s.Post.Quality.Shadows.Mode = ShadowMode.Off;
                });
                fx.Scene.TemporalHistory.CaptureDetailPathForTests = true;
                TemporalTestResolve.Install(fx, i == 0 ? Probe : Checked);
                fx.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
                draws[i] = TemporalResolveCostPerfGpuTests.FieldScene(stage);
            }
            output.WriteLine($"PROBE|{fixtures[0].Device.Backend}|{fixtures[0].Device.Capabilities.DeviceName}|{preset}|inputs {inputs}");
            int reported = 0;
            for (int frame = 0; frame < 24 && reported < 3; frame++)
            {
                foreach (TemporalFixture fx in fixtures) fx.Frames(1, draws[Array.IndexOf(fixtures, fx)]);
                byte[][] fine = Array.ConvertAll(fixtures, fx => TemporalVisibilityFullyLitGpuTests.Bytes(fx.Device,
                    fx.Scene.TemporalHistory.DetailStore!.Color(fx.Scene.TemporalHistory.WriteIndex)));
                float[][] path = Array.ConvertAll(fixtures, fx => TemporalTextureIo.Read(fx.Device,
                    fx.Scene.TemporalHistory.DetailStore!.TransportForTests));
                int fineWidth = (int)fixtures[0].Scene.TemporalHistory.DetailStore!.Color(0).Width;
                int displayWidth = fixtures[0].Scene.TemporalHistory.DisplayWidth;
                int differingBytes = 0, first = -1;
                for (int b = 0; b < fine[0].Length; b++)
                    if (fine[0][b] != fine[1][b]) { differingBytes++; if (first < 0) first = b; }
                var differingPath = new int[4];
                for (int v = 0; v < path[0].Length; v++)
                    if (BitConverter.SingleToUInt32Bits(path[0][v]) != BitConverter.SingleToUInt32Bits(path[1][v]))
                        differingPath[v % 4]++;
                string line = $"PROBE|frame {frame}|fine bytes differing {differingBytes}|path values differing "
                    + string.Join(",", differingPath);
                if (first >= 0)
                {
                    int texel = first / 8, channel = first % 8 / 2;
                    int x = texel % fineWidth, y = texel / fineWidth;
                    int pixel = (y / 2 * displayWidth + x / 2) * 4;
                    line += $"|first fine texel ({x},{y}) channel {channel}: certified 0x{BitConverter.ToUInt16(fine[0], first - first % 2):X4}"
                        + $" checked 0x{BitConverter.ToUInt16(fine[1], first - first % 2):X4}|display pixel ({x / 2},{y / 2})"
                        + $" certified {Floats(path[0], pixel)} checked {Floats(path[1], pixel)}";
                    reported++;
                }
                output.WriteLine(line);
            }
        }
        finally { foreach (TemporalFixture? fx in fixtures) fx?.Dispose(); }
    }

    static string Floats(float[] values, int at) => string.Join(" ",
        new[] { 0, 1, 2, 3 }.Select(c => $"{values[at + c]:R}(0x{BitConverter.SingleToUInt32Bits(values[at + c]):X8})"));
}
