using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class ReleaseProbeWaterGpuTests(MetalRecordingCapacityFixture fixture, ITestOutputHelper log)
    : IClassFixture<MetalRecordingCapacityFixture>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundWaterAndResizedViewportPreserveEightOutputsAndSameFrameCaptures(bool temporal)
    {
        byte[][]? control = null;
        foreach (var (slots, scene) in fixture.Modes)
        {
            foreach (bool split in new[] { false, true })
            {
                byte[][]? off = null;
                foreach (var mode in new[] { ShadowStageProbeMode.Off, ShadowStageProbeMode.Counts, ShadowStageProbeMode.Timings })
                {
                    var images = scene.Render(split, false, temporal, true, true, resize: true,
                        probeMode: mode, ringWaitProbe: mode != ShadowStageProbeMode.Off,
                        directionalMode: ShadowMode.ShadowMap, boundWater: true);
                    Assert.Equal(8, images.Length);
                    off ??= images;
                    control ??= images;
                    for (int i = 0; i < 8; i++)
                    {
                        Assert.Equal(control[i], images[i]);
                        Assert.Equal(off[i], images[i]);
                        if (mode == ShadowStageProbeMode.Off) continue;
                        var input = scene.ProbeSnapshots[i]!.Value.Inputs!.Value;
                        Assert.True(input.Complete);
                        Assert.Equal(i < 4 ? (64, 40) : (96, 64), (input.View.Width, input.View.Height));
                        Assert.Equal(slots, scene.WaitCallbacks[i].Main.Receipt!.Value.Depth);
                    }
                    log.WriteLine($"slots={slots}, split={split}, temporal={temporal}, mode={mode}, bound water prepared before recording, resized outputs and captures match exactly.");
                    log.WriteLine(scene.ValidationProof);
                }
            }
        }
    }
}
