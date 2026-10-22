using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfOutlineTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_outline_layout_survives_fully_dissolved_groups(bool dissolveAll)
    {
        using var device = new FakeGpuDevice();
        using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 32,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var target = device.Factory.CreateFramebuffer(null, texture);
        using var scene = new Scene3D(device, target.Outputs);
        using var commands = new GeometryTraceCommands(new NullGpuCommandList());
        var mesh = scene.LoadMesh(MeshPrimitives.Box(.5f));

        void Frame(bool dissolve, IGpuCommandList list)
        {
            commands.Events.Clear();
            scene.Begin();
            for (int groupIndex = 0; groupIndex < 2; groupIndex++)
            {
                var group = scene.BeginMeshOutline(Color.White, 1f);
                float amount = dissolve && (groupIndex == 0 || dissolveAll) ? 1f : 0f;
                scene.DrawMeshOutlineDissolved(group, mesh, Matrix4x4.Identity, amount, false);
                scene.DrawMeshOutlineDissolved(group, mesh, Matrix4x4.CreateTranslation(1f, 0f, 0f), amount, false);
            }
            scene.PrepareFrame();
            list.Begin();
            try { scene.RenderInternal(list, 32, 32, target); }
            finally { list.End(); }
        }

        Frame(false, commands);
        Frame(false, commands);
        var observer = new PostTraceObserver(commands);
        scene.PassObserver = observer;
        Frame(false, commands);
        observer.AssertClosed();
        string[] outlineIds = ["outline.uploads", "outline.mask", "outline.mask-resolve", "outline.mask-mips",
            "outline.visible-mask", "outline.visible-resolve", "outline.visible-mips", "outline.composite"];
        var definitions = observer.Freeze();
        for (int group = 0; group < 2; group++)
            Assert.Equal(outlineIds, definitions.Where(p => p.Id.StartsWith("outline.", StringComparison.Ordinal)
                && p.Occurrence == group).Select(p => p.Id));
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        scene.PassObserver = adapter;
        recorder.BeginFrame(0, 0, 0);
        adapter.BeginFrame();
        Frame(true, RendererPerfCommandList.Wrap(commands, recorder));
        recorder.EndFrame();

        var sample = recorder.Harvest(0);
        Assert.Empty(sample.Errors);
        for (int group = 0; group < (dissolveAll ? 2 : 1); group++)
            foreach (string id in outlineIds)
            {
                var pass = Assert.Single(sample.Passes, p => p.Id == id && p.Occurrence == group);
                Assert.Equal(RendererPerfMetricStatus.NotExecuted, pass.Duration.Status);
                Assert.Null(pass.Duration.Value);
                Assert.Null(pass.ElapsedTicks);
                int owner = Array.FindIndex(definitions, p => p.Id == id && p.Occurrence == group) + 1;
                foreach (RendererPerfCounter counter in Enum.GetValues<RendererPerfCounter>())
                    Assert.Equal(0, recorder.ReadCounter(0, counter, owner));
            }
        int maskOwner = Array.FindIndex(definitions, p => p.Id == "outline.mask" && p.Occurrence == 1) + 1;
        int compositeOwner = Array.FindIndex(definitions, p => p.Id == "outline.composite" && p.Occurrence == 1) + 1;
        Assert.Equal(dissolveAll ? 0 : 2, recorder.ReadCounter(0, RendererPerfCounter.IndexedDraws, maskOwner));
        Assert.Equal(dissolveAll ? 0 : 1, recorder.ReadCounter(0, RendererPerfCounter.Draws, compositeOwner));
    }
}
