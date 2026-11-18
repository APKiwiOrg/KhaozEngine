using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFineCostProbeGpuTests
{
    [GpuFact(RequiresRealGpu = true)]
    public void Exact_pan08_exercises_the_candidate_prefix()
    {
        using var context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
        Assert.Equal(GpuBackendKind.MetalNative, context.GpuDevice.Backend);
        foreach (var preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
        foreach (string path in new[] { "moving", "still", "pan08", "pan33" })
        {
            var stage = new FrontStage(Width, Height, 4.5f);
            using var fixture = new TemporalFixture(Width, Height, scene =>
            {
                stage.Setup(scene, AntiAliasing.Temporal, preset);
                scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
                scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.4f, -0.6f, -1f));
                scene.Post.AmbientColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                scene.TemporalHistory.CaptureDetailPathForTests = true;
                scene.InstallTemporalResolveForTests(ObserveMotion);
            }, context.GpuDevice);
            fixture.Frames(Warm + 1, DrawPath(stage, path));
            Assert.Equal(TemporalHistoryDetail.Fine, fixture.Scene.TemporalHistoryDetailInEffect);
            float[] observed = TemporalTextureIo.Read(fixture.Device, fixture.Scene.TemporalHistory.DetailStore!.TransportForTests);
            int valid = 0, still = 0, prefix = 0, fine = 0;
            for (int i = 0; i < observed.Length; i += 4)
            {
                if (observed[i + 2] > 0.5f) valid++;
                if (observed[i] < 1f / 128f) still++;
                if (observed[i + 1] > 0.5f) prefix++;
                if (observed[i + 3] > 0.5f) fine++;
            }
            output.WriteLine($"MOTION|{preset}|{path}|valid={valid}|still={still}|prefix={prefix}|fine={fine}|texels={Width * Height}");
            if (path == "pan08") Assert.True(prefix > 0, "The small pan must reach the live slow-motion prefix.");
        }
    }

    static string ObserveMotion(string source)
    {
        const string original = "    oTransport = detailTransport;";
        if (!source.Contains(original, StringComparison.Ordinal)) return source;
        string updated = TemporalFineExactPrograms.Replace(source, "ivec2 detailPixel;", "ivec2 detailPixel;\nvec3 probeMotion;");
        updated = TemporalFineExactPrograms.Replace(updated, "    detailPixel = pixel;",
            "    detailPixel = pixel;\n    probeMotion = vec3(0.0);");
        updated = TemporalFineExactPrograms.Replace(updated, "        bool fine = detailGate(motionPixels);", """
        bool fine = detailGate(motionPixels);
        probeMotion = vec3(motionPixels, fine && !detailUseFine(motionPixels, true, 0u, 0u) ? 1.0 : 0.0, 1.0);
""");
        return TemporalFineExactPrograms.Replace(updated, original, "    oTransport = vec4(probeMotion, detailTransport.w);");
    }
}
