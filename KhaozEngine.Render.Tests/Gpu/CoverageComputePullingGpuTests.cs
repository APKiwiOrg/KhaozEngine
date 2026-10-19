using System;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed partial class CoverageComputePullingGpuTests(ITestOutputHelper output, CoveragePullingGpuFixture fixture) : IClassFixture<CoveragePullingGpuFixture>
{
    [GpuFact]
    public void Main_and_both_outline_masks_pull_one_selected_surface()
        => Check(refusal: false);

    [GpuFact]
    public void Early_refinement_refusal_draws_original_triangles_in_all_materials()
        => Check(refusal: true);

    [GpuFact]
    public void Alpha_cutout_and_both_dissolve_senses_use_the_shared_pulled_materials()
        => Check(refusal: false, materialEffects: true);

    void Check(bool refusal, bool materialEffects = false)
    {
        var gpu = fixture.Device;
        var scene = fixture.Scene;
        scene.Select(refusal, materialEffects);
        var reference = scene.Capture(coverage: false, gpuSkinning: true, outlineOnly: false);
        var actual = scene.Capture(coverage: true, gpuSkinning: false, outlineOnly: false);
        string path = "/tmp/grimhollow-equipment-coverage-task10";
        Directory.CreateDirectory(path);
        string stem = refusal ? "refusal" : materialEffects ? "materials" : "covered";
        File.WriteAllBytes(Path.Combine(path, stem + ".rgba"), actual.Colour);
        File.WriteAllBytes(Path.Combine(path, stem + ".reference.rgba"), reference.Colour);
        File.WriteAllText(Path.Combine(path, stem + ".bindings.json"), scene.Bindings.Json());
        Assert.False(MemoryMarshal.AsBytes(scene.Actors[0].Posed.AsSpan()).SequenceEqual(
            MemoryMarshal.AsBytes(scene.Actors[1].Posed.AsSpan())));
        scene.Bindings.AssertMatches(scene.Actors, scene.Scene.CoverageGpuBatchForTests!, outlineOnly: false);
        Assert.Equal(reference.Colour, actual.Colour);
        Assert.Equal(reference.Full.Values, actual.Full.Values);
        Assert.Equal(reference.Visible.Values, actual.Visible.Values);
        Assert.Contains(actual.Full.Values, v => v > 0);
        Assert.Contains(actual.Visible.Values, v => v > 0);
        Assert.Equal(2, scene.Scene.DispatchedHostPreparationCount);
        Assert.Equal(0, scene.Scene.CoverageDiagnosticsForTests.UncoveredGpuDraws);
        Assert.Equal(0, scene.Scene.OrdinaryCpuSkinnedVerticesForTests);
        Assert.Equal(0, scene.Scene.CoveredHostPreparationCount);
        var batch = scene.Scene.CoverageGpuBatchForTests!;
        uint[] words = Read(gpu, batch.Current);
        uint[] work = Read(gpu, batch.Work);
        foreach (var actor in scene.Actors)
        {
            var r = actor.Frame.Preparation!.GpuReservation;
            Assert.True(actor.Topology.MaterialRanges[1].FirstIndex > 0);
            Assert.Equal(actor.Frame.Preparation.ResultIdentity.AsSpan(0, 8).ToArray(),
                words.AsSpan((int)r.CurrentBase, 8).ToArray());
            Assert.Equal(refusal ? 1u : 0u, words[r.CurrentBase + 14]);
            if (refusal)
            {
                Assert.Equal((uint)CoverageFailureCode.UnsupportedTransform, words[r.CurrentBase + 8]);
                Assert.Equal(5u, work[r.WorkBase + CoverageGpuLayout.FailureStage]);
                Assert.Equal(0u, work[r.WorkBase + CoverageGpuLayout.BeforePublished]);
            }
            var expected = refusal ? actor.Posed : actor.Final;
            int start = checked((int)r.CurrentBase + 16 + (refusal ? 0 : r.SourceCount * 16));
            Assert.Equal(MemoryMarshal.Cast<ModelVertex, uint>(expected).ToArray(), words.AsSpan(start, expected.Length * 16).ToArray());
        }

        var outlineReference = scene.Capture(coverage: false, gpuSkinning: false, outlineOnly: true);
        var outlineActual = scene.Capture(coverage: true, gpuSkinning: true, outlineOnly: true);
        File.WriteAllText(Path.Combine(path, stem + ".outline-only.bindings.json"), scene.Bindings.Json());
        scene.Bindings.AssertMatches(scene.Actors, scene.Scene.CoverageGpuBatchForTests!, outlineOnly: true);
        Assert.Equal(outlineReference.Colour, outlineActual.Colour);
        Assert.Equal(outlineReference.Full.Values, outlineActual.Full.Values);
        Assert.Equal(outlineReference.Visible.Values, outlineActual.Visible.Values);
        File.WriteAllBytes(Path.Combine(path, stem + ".full.f32"), MemoryMarshal.AsBytes(actual.Full.Values.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(path, stem + ".visible.f32"), MemoryMarshal.AsBytes(actual.Visible.Values.AsSpan()).ToArray());
        output.WriteLine($"{gpu.Backend} {gpu.Capabilities.DeviceName}: two actors, two materials, nonzero firstCorner, main/full/visible and outline-only exact pixels. refusal={refusal}. Actual main/full/visible resources, dynamic offsets and uploaded window words match. Final word mismatches=0, fallback extra pixels=0, covered CPU uploads=0, ordinary host draws=0.");
    }

    [GpuFact]
    public void Shared_scene_after_material_and_refusal_views_matches_a_fresh_scene()
    {
        var shared = fixture.Scene;
        shared.Select(true, false); _ = shared.Capture(true, false, true);
        shared.Select(false, true); _ = shared.Capture(true, true, false);
        shared.Select(false, false);
        var actual = shared.Capture(true, false, false);
        using var fresh = new CoveragePullingTestScene(fixture.Device);
        var expected = fresh.Capture(true, false, false);
        Assert.Equal(expected.Colour, actual.Colour);
        Assert.Equal(expected.Full.Values, actual.Full.Values);
        Assert.Equal(expected.Visible.Values, actual.Visible.Values);
    }

    static unsafe uint[] Read(IGpuDevice device, IGpuBuffer buffer)
    {
        using var staging = device.Factory.CreateBuffer(new(buffer.SizeInBytes, GpuBufferUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        commands.Begin(); commands.CopyBuffer(buffer, 0, staging, 0, buffer.SizeInBytes); commands.End();
        device.Submit(commands); device.WaitForIdle();
        var map = device.Map(staging, GpuMapMode.Read);
        try { return new ReadOnlySpan<uint>((void*)map.Data, checked((int)buffer.SizeInBytes / 4)).ToArray(); }
        finally { device.Unmap(staging); }
    }
}

public sealed class CoveragePullingGpuFixture : IDisposable
{
    GpuDeviceContext? gpu;
    CoveragePullingTestScene? scene;
    internal IGpuDevice Device => (gpu ??= GpuDeviceContext.CreateHeadless()).GpuDevice;
    internal CoveragePullingTestScene Scene => scene ??= new(Device);
    public void Dispose() { scene?.Dispose(); gpu?.Dispose(); }
}
