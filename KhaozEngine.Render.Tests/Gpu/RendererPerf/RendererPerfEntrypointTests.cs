using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("AllocSensitive")]
public sealed class RendererPerfEntrypointTests
{
    [Theory]
    [InlineData("S0", "empty")]
    [InlineData("S1", "village")]
    [InlineData("S2", "outdoors")]
    [InlineData("S3", "crowd")]
    [InlineData("S4", "effects-ui")]
    public void Frozen_layout_records_cold_and_cached_routes_without_native_discovery(string scene, string name)
    {
        var identity = Identity(scene, name);
        var passes = RendererPerfExecutable.Layout(identity);
        Assert.Contains(passes, p => p.Id == "device.scene.begin" && p.DeviceOnly);
        Assert.Contains(passes, p => p.Id == "post.blit");
        using var device = new PostTraceDevice();
        var recorder = new RendererPerfRecorder(4, RendererPerfLayout.PerPass, passes, () => 0, 1000);
        var observer = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, passes);
        var trace = new GeometryTraceCommands(device.Factory.CreateCommandList());
        var relay = new TraceObserver(observer, new RendererPerfGeometryTraceTests.GeometryObserver(trace), recorder);
        using var work = new RendererPerfSceneWorkload(RendererPerfDevice.Wrap(device, recorder), relay, identity, (_, _) => { });
        work.PrepareCold(-192);
        using var commands = RendererPerfCommandList.Wrap(trace, recorder);
        recorder.FrameCommands = RendererPerfCommandList.Unwrap(commands);
        for (int frame = 0; frame < 4; frame++)
        {
            relay.Trace = new RendererPerfGeometryTraceTests.GeometryObserver(trace);
            relay.Frame = frame;
            observer.BeginFrame();
            recorder.BeginFrame(frame, frame, frame);
            work.Prepare(-192 + frame);
            commands.Begin();
            work.Record(commands, -192 + frame);
            commands.End();
            recorder.EndFrame();
            var sample = recorder.Harvest(frame);
            if (!sample.Errors.IsEmpty && Environment.GetEnvironmentVariable("KE_RENDERER_PERF_TASK13_EVIDENCE") is { } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, name + "-layout-" + frame + ".json"),
                    JsonSerializer.SerializeToUtf8Bytes(new { frame, defined = passes, observed = relay.Trace.Freeze(), sample.Errors, relay.FailureSite }));
            }
            Assert.Empty(sample.Errors);
            if (scene == "S3" && frame == 1)
            {
                var transient = sample.Passes.Where(p => p.Id.StartsWith("shadow.point.transient", StringComparison.Ordinal)).ToArray();
                Assert.Equal(26, transient.Length);
                Assert.All(transient, pass =>
                {
                    Assert.Equal(RendererPerfMetricStatus.NotExecuted, pass.Duration.Status);
                    Assert.Null(pass.ElapsedTicks);
                    Assert.Null(pass.Duration.Value);
                });
            }
        }
    }

    [Fact]
    public void Changed_work_is_refused_before_native_creation()
    {
        var manifest = RendererPerfManifest.Parse(System.Text.Encoding.UTF8.GetBytes(RendererPerfProtocolTests.ManifestJson().ToJsonString()));
        manifest = manifest with
        {
            Recipes = [Identity("S3", "crowd")],
            Controls = [.. manifest.Controls.Select(c => c with { WorkSha256 = RendererPerfControls.Definition(c.Id).WorkSha256 })]
        };
        RendererPerfExecutable.ValidateWork(manifest);
        var changed = manifest with { Recipes = [manifest.Recipes[0] with { ShaderSha256 = new string('f', 64) }] };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfExecutable.ValidateWork(changed));
        changed = manifest with { Controls = manifest.Controls.SetItem(4, manifest.Controls[4] with { WorkSha256 = new string('f', 64) }) };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfExecutable.ValidateWork(changed));
    }

    internal static RendererPerfRecipeIdentity Identity(string scene, string name)
    {
        using var recipe = RendererPerfRecipe.Create(name);
        var f = recipe.Facts;
        return new(scene, name + "/1", f.Profile, f.RecipeSha256, f.SettingsSha256,
            RendererPerfExecutable.ShaderSha256, f.GeometrySha256, f.AssetSha256);
    }

    sealed class TraceObserver(IRenderPassObserver inner, RendererPerfGeometryTraceTests.GeometryObserver trace, RendererPerfRecorder recorder) : IRenderPassObserver
    {
        internal RendererPerfGeometryTraceTests.GeometryObserver Trace = trace;
        internal int Frame;
        internal string? FailureSite;
        void Inspect(string site, IGpuCommandList commands)
        {
            if (FailureSite is not null || recorder.FrameFailure(Frame) is null) return;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            FailureSite = site + ", host=" + ReferenceEquals(RendererPerfCommandList.Unwrap(commands), recorder.FrameCommands)
                + ", marker=" + typeof(RendererPerfRecorder).GetField("_nextMarker", flags)!.GetValue(recorder);
        }
        public void BeginPass(IGpuCommandList commands, RenderPassKey key) { Trace.BeginPass(commands, key); inner.BeginPass(commands, key); Inspect("begin " + key, commands); }
        public void EndPass(IGpuCommandList commands, RenderPassKey key) { inner.EndPass(commands, key); Trace.EndPass(commands, key); Inspect("end " + key, commands); }
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) { Trace.SkipPass(commands, key); inner.SkipPass(commands, key); Inspect("skip " + key, commands); }
        public void BeginCpuPhase(string id) { Trace.BeginCpuPhase(id); inner.BeginCpuPhase(id); }
        public void EndCpuPhase(string id) { inner.EndCpuPhase(id); Trace.EndCpuPhase(id); }
        public void FusedMotion(IGpuCommandList commands, RenderPassKey key) { Trace.FusedMotion(commands, key); inner.FusedMotion(commands, key); }
    }
}
