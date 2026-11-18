using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFineCostProbeGpuTests
{
    [GpuFact(RequiresRealGpu = true)]
    public void Exact_classifier_checks_invalid_history()
    {
        using var context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
        var device = context.GpuDevice;
        Assert.Equal(GpuBackendKind.MetalNative, device.Backend);
        var cases = new[]
        {
            new HistoryBytes("finite", 0x3800, 0x3c00, 0),
            new HistoryBytes("near-maximum", 0x7bfe, 0x3c00, 0),
            new HistoryBytes("positive-maximum", 0x7bff, 0x3c00, 0),
            new HistoryBytes("negative-maximum", 0xfbff, 0x3c00, 0),
            new HistoryBytes("positive-infinity", 0x7c00, 0x3c00, 0),
            new HistoryBytes("negative-infinity", 0xfc00, 0x3c00, 0),
            new HistoryBytes("nan", 0x7e00, 0x3c00, 0),
            new HistoryBytes("maximum-state", 0x3800, 0x7bff, 0),
            new HistoryBytes("nonfinite-record", 0x3800, 0x3c00, 0x7c00),
            new HistoryBytes("mixed-lattice", 0x3800, 0x3c00, 0, true),
        };
        foreach (bool pan in new[] { false, true })
        {
            using var control = ValidityArm(device, null, out var draw, pan);
            using var trial = ValidityArm(device, [TemporalFineExactLever.StateBeforeColor, TemporalFineExactLever.SharedClamp],
                out var drawTrial, pan);
            control.Frames(Warm, draw);
            trial.Frames(Warm, drawTrial);
            foreach (var payload in cases)
            {
                Seed(control, payload);
                Seed(trial, payload);
                control.Frames(1, draw);
                trial.Frames(1, drawTrial);
                byte[] reference = Classification(control), variant = Classification(trial);
                output.WriteLine($"VALIDITY|{(pan ? "pan08" : "still")}|{payload.Name}"
                    + $"|reference={Convert.ToHexString(SHA256.HashData(reference))}"
                    + $"|variant={Convert.ToHexString(SHA256.HashData(variant))}"
                    + $"|refused={reference.Count(b => b == 1)}|texels={reference.Length}");
                Assert.Equal(reference, variant);
                int centre = control.DisplayHeight / 2 * control.DisplayWidth + control.DisplayWidth / 2;
                if (payload.Name == "finite") Assert.Equal((byte)0, reference[centre]);
                if (payload.Name is "positive-maximum" or "negative-maximum" or "maximum-state" or "nonfinite-record")
                    Assert.Equal((byte)1, reference[centre]);
            }
        }
    }

    static TemporalFixture ValidityArm(IGpuDevice device, TemporalFineExactLever[]? levers,
        out Action<Scene3D, int> draw, bool pan)
    {
        var stage = new FrontStage(64, 48, 4.5f);
        var fixture = new TemporalFixture(stage.W, stage.H, scene =>
        {
            stage.Setup(scene, AntiAliasing.Temporal);
            scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
            if (levers is not null)
                scene.InstallTemporalResolveForTests(source => TemporalFineExactPrograms.Rewrite(source, levers, default));
        }, device);
        draw = (scene, frame) =>
        {
            scene.Camera.Target = new Vector3(pan ? frame * stage.PixelWorld * 0.08f : 0f, 0f, 0f);
            stage.TexturedWall(scene);
        };
        return fixture;
    }

    static byte[] Classification(TemporalFixture fixture) =>
        TemporalVisibilityHistoryGpuTests.Read(fixture.Device,
            fixture.Scene.TemporalHistory.DetailStore!.ClassificationForTests);

    static void Seed(TemporalFixture fixture, HistoryBytes historyBytes)
    {
        var history = fixture.Scene.TemporalHistory;
        int index = history.WriteIndex;
        WriteHistory(fixture.Device, history.Color(index), historyBytes.Color, historyBytes.Color, historyBytes.Color, historyBytes.Color,
            historyBytes.Mixed);
        WriteHistory(fixture.Device, history.DetailStore!.Color(index), historyBytes.Color, historyBytes.Color,
            historyBytes.Color, historyBytes.Color, historyBytes.Mixed);
        WriteHistory(fixture.Device, history.Confidence(index), historyBytes.Confidence, 0, historyBytes.Record, 0, false);
    }

    static void WriteHistory(IGpuDevice device, IGpuTexture texture, ushort r, ushort g, ushort b, ushort a, bool mixed)
    {
        int width = (int)texture.Width, height = (int)texture.Height;
        byte[] bytes = new byte[width * height * 8];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var texel = bytes.AsSpan((y * width + x) * 8, 8);
            ushort first = mixed ? ((x + 3 * y) % 11) switch { 0 => (ushort)0x7c00, 1 => (ushort)0x7e00, 2 => (ushort)0x7bff, _ => r } : r;
            BinaryPrimitives.WriteUInt16LittleEndian(texel, first);
            BinaryPrimitives.WriteUInt16LittleEndian(texel[2..], g);
            BinaryPrimitives.WriteUInt16LittleEndian(texel[4..], b);
            BinaryPrimitives.WriteUInt16LittleEndian(texel[6..], a);
        }
        device.UpdateTexture(texture, bytes, 0, 0, texture.Width, texture.Height);
    }

    readonly record struct HistoryBytes(string Name, ushort Color, ushort Confidence, ushort Record, bool Mixed = false);
}
