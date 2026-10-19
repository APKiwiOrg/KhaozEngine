using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FramePreparationAdmissionTests
{
    [Fact]
    public void Coverage_without_water_requires_pre_record_admission_and_keeps_the_prepared_no_op()
    {
        using var device = new OpenListTrackingGpuDevice();
        using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var target = device.Factory.CreateFramebuffer(null, texture);
        using var scene = new Scene3D(device, target.Outputs);
        using var actor = new CoveragePullingTestScene.Actor(scene, 0, false, false);
        using var commands = device.Factory.CreateCommandList();
        scene.Begin(); actor.Draw(true, false, outlines: false);
        using (GpuRecording.Open(device, commands, "coverage outer recording"))
        {
            int begins = device.Begins, textures = device.TexturesCreated;
            var error = Assert.Throws<GpuNestedRecordingException>(() => scene.PrepareFrame());
            Assert.Equal("coverage outer recording", error.Owner);
            Assert.Equal("Scene3D.PrepareFrame", error.Attempted);
            Assert.Equal(begins, device.Begins);
            Assert.Equal(textures, device.TexturesCreated);
            Assert.Equal(1, device.OpenLists);
        }
        scene.PrepareFrame();
        using (GpuRecording.Open(device, commands, "prepared outer recording"))
        {
            int begins = device.Begins;
            scene.PrepareFrame();
            Assert.Equal(begins, device.Begins);
            Assert.Equal(1, device.OpenLists);
        }
    }

    [Fact]
    public void Nested_preview_refusal_preserves_the_outer_recording_and_queued_scene()
    {
        using var device = new OpenListTrackingGpuDevice();
        using var preview = new Render3DPreview(device, 32, 24);
        using var commands = device.Factory.CreateCommandList();
        preview.Scene.DrawWater(new WaterPlane(0, 0, 0, 8));
        bool called = false;
        using (GpuRecording.Open(device, commands, "preview outer recording"))
        {
            int begins = device.Begins;
            var error = Assert.Throws<GpuNestedRecordingException>(() => preview.Capture(_ => called = true));
            Assert.Equal("preview outer recording", error.Owner);
            Assert.Equal("Render3DPreview.Capture", error.Attempted);
            Assert.False(called);
            Assert.Equal(1, preview.Scene.WaterPlaneCount);
            Assert.Equal(begins, device.Begins);
            Assert.Equal(1, device.OpenLists);
        }
    }

    [Fact]
    public void Direct_ocean_producer_keeps_its_own_named_nested_recording_refusal()
    {
        using var device = new OpenListTrackingGpuDevice();
        using var producer = new OceanFftProducer(device);
        using var commands = device.Factory.CreateCommandList();
        var settings = new WaterSettings { WaveSource = WaterWaveSource.FftOcean };
        settings.SeaState.CascadeCount = 1; settings.SeaState.CascadeResolution = 32;
        using (GpuRecording.Open(device, commands, "ocean outer recording"))
        {
            int begins = device.Begins;
            var error = Assert.Throws<GpuNestedRecordingException>(() => producer.Prepare(settings, 0, wantOcean: true));
            Assert.Equal("ocean outer recording", error.Owner);
            Assert.Contains("FFT ocean", error.Attempted);
            Assert.Equal(begins, device.Begins);
            Assert.Equal(1, device.OpenLists);
        }
    }
}
