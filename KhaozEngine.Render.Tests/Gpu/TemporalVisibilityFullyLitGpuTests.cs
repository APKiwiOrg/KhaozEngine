using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityFullyLitGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
{
    [GpuTheory]
    [InlineData(TemporalUpscale.Native, 0)]
    [InlineData(TemporalUpscale.Quality, 0)]
    [InlineData(TemporalUpscale.Native, 1)]
    [InlineData(TemporalUpscale.Quality, 1)]
    [InlineData(TemporalUpscale.Native, 2)]
    [InlineData(TemporalUpscale.Quality, 2)]
    public void CertifiedScenesMatchTheCheckAndBothEntriesOnEveryHistoryByte(TemporalUpscale preset, int scene)
    {
        // Four independent histories must advance in lockstep. None can share the scene whose bytes it checks.
        var stages = new FrontStage[4];
        var fixtures = new TemporalFixture[4];
        var draws = new Action<Scene3D, int>[4];
        try
        {
            for (int i = 0; i < fixtures.Length; i++)
            {
                var stage = stages[i] = new FrontStage(320, 180, 4.5f);
                var fx = fixtures[i] = new TemporalFixture(320, 180, s =>
                {
                    stage.Setup(s, AntiAliasing.Temporal, preset);
                    s.Post.Quality.Shadows.Mode = ShadowMode.Off;
                    s.Post.Outline = scene == 2;
                });
                if (i >= 2) TemporalTestResolve.Install(fx, ForceCheck);
                fx.Scene.TemporalResolveEntryForTests = i % 2 == 0 ? TemporalResolveEntry.Fused : TemporalResolveEntry.Split;
                draws[i] = scene == 0 ? TemporalResolveCostPerfGpuTests.BoxesScene(stage)
                    : TemporalResolveCostPerfGpuTests.FieldScene(stage);
            }
            output.WriteLine($"{fixtures[0].Device.Backend} {fixtures[0].Device.Capabilities.DeviceName}, {preset}, scene {scene}");
            var oracle = Oracle(fixtures[0].Device);
            for (int frame = 0; frame < 24; frame++)
            {
                for (int i = 0; i < fixtures.Length; i++)
                {
                    TemporalFixture fx = fixtures[i];
                    fx.Frames(1, draws[i]);
                    TemporalResolveRenderer renderer = fx.Scene.TemporalResolveRendererForTests!;
                    Assert.True(renderer.LastCurrentVisibilityFullyLit);
                    Assert.Equal(frame > 0, renderer.LastVisibilityDecisionSkipped);
                    Assert.All(TemporalVisibilityHistoryGpuTests.Read(fx.Device, fx.Scene.ResolvedVisibilityTarget!),
                        b => Assert.Equal(255, b));
                }
                TemporalFullyLitHistoryOracle.Snapshot[] samples = Array.ConvertAll(fixtures,
                    TemporalFullyLitHistoryOracle.Snapshot.Read);
                oracle.Report(frame, "fused certified vs check", samples[0], samples[2]);
                oracle.Report(frame, "split certified vs check", samples[1], samples[3]);
                oracle.Report(frame, "certified fused vs split", samples[0], samples[1], entryComparison: true);
            }
            oracle.AssertComplete();
        }
        finally { foreach (TemporalFixture? fx in fixtures) fx?.Dispose(); }
    }

    [GpuTheory]
    [InlineData(TemporalUpscale.Native, 0)]
    [InlineData(TemporalUpscale.Quality, 0)]
    [InlineData(TemporalUpscale.Native, 1)]
    [InlineData(TemporalUpscale.Quality, 1)]
    public void DebugViewsAndCountReevaluationMatchTheForcedCheck(TemporalUpscale preset, int choice)
    {
        var a = new FrontStage(160, 90, 4.5f);
        var b = new FrontStage(160, 90, 4.5f);
        using var selected = new TemporalFixture(160, 90, s => a.Setup(s, AntiAliasing.Temporal, preset));
        using var checkedFx = new TemporalFixture(160, 90, s => b.Setup(s, AntiAliasing.Temporal, preset));
        TemporalTestResolve.Install(checkedFx, ForceCheck);
        selected.Scene.TemporalResolveEntryForTests = checkedFx.Scene.TemporalResolveEntryForTests = (TemporalResolveEntry)choice;
        var drawA = TemporalResolveCostPerfGpuTests.FieldScene(a);
        var drawB = TemporalResolveCostPerfGpuTests.FieldScene(b);
        var oracle = Oracle(selected.Device);
        // Frames 0 to 5 cycle History, Disocclusion and Reactive. Shimmer, whose score reads the shadow gate only in
        // the checked program, takes the rest. The record steps 1 / PhaseCount a frame, 1/18 at Quality, so shadows
        // stay off for one jitter cycle first and the field shimmers at every preset. Shadows then go on and off over
        // two frames, where both fixtures run the check and the visibility moves. On the last frame the selected
        // fixture skips the decision and runs the program without the gate. The visibility is fully lit there and on
        // the frame before, so the gate finds no change and both images still agree exactly. The warm-up frames skip
        // the decision the same way.
        int shadowsOn = 6 + TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset));
        int last = shadowsOn + 2;
        SceneDebugView[] cycle = [SceneDebugView.History, SceneDebugView.Disocclusion, SceneDebugView.Reactive];
        for (int frame = 0; frame <= last; frame++)
        {
            selected.Scene.Post.Quality.Shadows.Mode = checkedFx.Scene.Post.Quality.Shadows.Mode
                = frame == 3 || frame == shadowsOn ? ShadowMode.ShadowMap : ShadowMode.Off;
            selected.Scene.DebugView = checkedFx.Scene.DebugView = frame < 6 ? cycle[frame % 3] : SceneDebugView.Shimmer;
            selected.Scene.RequestTemporalCounts();
            checkedFx.Scene.RequestTemporalCounts();
            byte[] selectedImage = selected.Frame(drawA), checkedImage = checkedFx.Frame(drawB);
            TemporalHistorySample.Exact(frame, "debug image", selectedImage, checkedImage);
            if (frame == last)
            {
                // Two blank Shimmer images would also agree, so both must mark the field's shimmering texels.
                int selectedTinted = TintedPixels(selectedImage), checkedTinted = TintedPixels(checkedImage);
                output.WriteLine($"frame {frame} shimmer tinted pixels: selected {selectedTinted}, checked {checkedTinted}");
                Assert.True(selectedTinted > 0, "the selected fixture's Shimmer view must tint the field");
                Assert.True(checkedTinted > 0, "the checked fixture's Shimmer view must tint the field");
            }
            oracle.Report(frame, "debug and counts", TemporalFullyLitHistoryOracle.Snapshot.Read(selected),
                TemporalFullyLitHistoryOracle.Snapshot.Read(checkedFx));
            Assert.Equal(frame is 1 or 2 or 5 || frame >= 6 && frame != shadowsOn && frame != shadowsOn + 1,
                selected.Scene.TemporalResolveRendererForTests!.LastVisibilityDecisionSkipped);
            Assert.Equal(selected.Scene.LastTemporalDiagnostics.DisoccludedPixels, checkedFx.Scene.LastTemporalDiagnostics.DisoccludedPixels);
            Assert.Equal(selected.Scene.LastTemporalDiagnostics.ReactivePixels, checkedFx.Scene.LastTemporalDiagnostics.ReactivePixels);
            Assert.Equal(selected.Scene.LastTemporalDiagnostics.ClippedPixels, checkedFx.Scene.LastTemporalDiagnostics.ClippedPixels);
        }
        oracle.AssertComplete();
        Assert.True(selected.Scene.TemporalCountReadbacksForTests > 0);
        Assert.True(selected.Scene.TemporalDebugViewBuiltForTests);

        static int TintedPixels(byte[] rgba)
        {
            int tinted = 0;
            for (int y = 0; y < 90; y++)
                for (int x = 0; x < 160; x++)
                    if (ShimmerViewTint.BlueExcess(rgba, 160, x, y) >= ShimmerViewTint.Tinted) tinted++;
            return tinted;
        }
    }

    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void OnlyTheOwnedFallbackCanCertifyAnAllWhiteSyntheticResolve(int choice, bool fallback)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)choice, retired);
        using IGpuTexture signal = rig.Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            N, N, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
        rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => Vector2.Zero));
        for (int frame = 0; frame < 3; frame++)
        {
            var bytes = new byte[N * N];
            if (frame < 2 || fallback) Array.Fill(bytes, byte.MaxValue);
            rig.Device.UpdateTexture(signal, bytes, 0, 0, N, N);
            rig.BeginFrame();
            rig.Resolve(Uniforms(frame > 0), fallback ? null : signal);
            Assert.Equal(bytes, TemporalVisibilityHistoryGpuTests.Read(rig.Device,
                rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex)));
            Assert.Equal(fallback, rig.Renderer.LastCurrentVisibilityFullyLit);
            Assert.Equal(fallback && frame > 0, rig.Renderer.LastVisibilityDecisionSkipped);
            rig.History.MarkValidAfterFrame();
        }
    }

    // Restore the complete ordinary program, including the declarations and helpers the certified source omits.
    // It is independently compiled through the existing hook with its full resource layout.
    internal static string ForceCheck(string source)
    {
        if (!source.Contains("bool visibilityReleased = false;", StringComparison.Ordinal)) return source;
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            if (program.Name.StartsWith("Temporal", StringComparison.Ordinal)
                && !program.Name.EndsWith("FullyLit", StringComparison.Ordinal)
                && source == TemporalVisibilityPrograms.WithoutDecision(program.FragmentGlsl))
                return program.FragmentGlsl;
        throw new InvalidOperationException("The forced check requires a known certified temporal program.");
    }

    TemporalFullyLitHistoryOracle Oracle(IGpuDevice device) => new(output, TemporalDeviceClass.IsLavapipe(device),
        TemporalDeviceClass.IsWarp(device), TemporalDeviceClass.IsHardwareNvidiaD3D11(device));

    internal static byte[] Bytes(IGpuDevice device, IGpuTexture source)
    {
        int stride = source.Format switch
        {
            GpuPixelFormat.R16G16B16A16Float => 8,
            GpuPixelFormat.R16G16Float or GpuPixelFormat.R32Float => 4,
            GpuPixelFormat.R8UNorm => 1,
            _ => throw new InvalidOperationException($"Unexpected history format {source.Format}"),
        };
        using IGpuTexture staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            source.Width, source.Height, source.Format, GpuTextureUsage.Staging));
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, nameof(TemporalVisibilityFullyLitGpuTests))) commands.CopyTexture(source, staging);
        device.Submit(commands);
        device.WaitForIdle();
        int rowBytes = checked((int)source.Width * stride);
        var bytes = new byte[checked(rowBytes * (int)source.Height)];
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < source.Height; y++)
                Marshal.Copy(map.Data + checked(y * (int)map.RowPitch), bytes, y * rowBytes, rowBytes);
        }
        finally { device.Unmap(staging); }
        return bytes;
    }
}
