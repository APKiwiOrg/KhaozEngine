using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed partial class CoverageComputeShadowGpuTests(ITestOutputHelper output, CoverageShadowPullingFixture fixture)
    : IClassFixture<CoverageShadowPullingFixture>
{
    [GpuTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void Every_cascade_and_point_face_uses_the_selected_surface(bool staticLight, bool refusal, bool effects)
    {
        Assert.NotNull(typeof(ModelRenderer).GetMethod("DrawCoverageCascade", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(PointShadowRenderer).GetMethod("DrawCoverageCaster", BindingFlags.Instance | BindingFlags.NonPublic));
        var scene = fixture.Scene;
        scene.Select(refusal, effects);
        var expected = scene.CaptureShadows(false, staticLight, shadowOnly: true, cameraCulled: true);
        var actual = scene.CaptureShadows(true, staticLight, shadowOnly: true, cameraCulled: true);
        Assert.Equal(4, actual.Cascades.Width / actual.Cascades.Height);
        Assert.Equal(6, actual.Points.Width / actual.Points.Height);
        CoveragePixelProof.EqualTarget(actual.Cascades, expected.Cascades, CoveragePixelProof.DepthTolerance, "four cascades");
        CoveragePixelProof.EqualTarget(actual.Points, expected.Points, CoveragePixelProof.DepthTolerance, "six point faces");
        AssertTiles(actual.Cascades, expected.Cascades, 4);
        AssertTiles(actual.Points, expected.Points, 6);
        var batch = scene.Scene.CoverageGpuBatchForTests!;
        uint[] words = CoverageSelectedReadback.Read(fixture.Device, batch.Current);
        uint[] work = refusal ? CoverageSelectedReadback.Read(fixture.Device, batch.Work) : [];
        Assert.False(MemoryMarshal.AsBytes(scene.Actors[0].Posed.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(scene.Actors[1].Posed.AsSpan())));
        foreach (var actor in scene.Actors)
        {
            output.WriteLine($"actor={actor.Key.Value} frozenSphere={actor.Frame.Preparation!.Sphere}");
            var p = actor.Frame.Preparation!;
            var r = p.GpuReservation;
            Assert.Equal(p.ResultIdentity.AsSpan(0, 8).ToArray(), words.AsSpan((int)r.CurrentBase, 8).ToArray());
            Assert.Equal(refusal ? 1u : 0u, words[r.CurrentBase + 14]);
            Assert.Equal(refusal ? (uint)CoverageFailureCode.UnsupportedTransform : 0u, words[r.CurrentBase + 8]);
            if (refusal)
            {
                Assert.Equal(5u, work[r.WorkBase + CoverageGpuLayout.FailureStage]);
                Assert.Equal(0u, work[r.WorkBase + CoverageGpuLayout.BeforePublished]);
            }
            var reference = refusal ? actor.Posed : actor.Final;
            Assert.Equal(MemoryMarshal.Cast<ModelVertex, uint>(reference).ToArray(),
                words.AsSpan(checked((int)r.CurrentBase + 16 + (refusal ? 0 : r.SourceCount * 16)), reference.Length * 16).ToArray());
        }
        foreach (string pass in new[] { "cascade", "point" })
        {
            int cells = pass == "cascade" ? 4 : 6;
            var draws = scene.Bindings.Draws.Where(d => d.Pass == pass).ToArray();
            int perCell = pass == "cascade" ? 20 : 18;
            Assert.Equal(cells * perCell, draws.Length);
            for (int cell = 0; cell < cells; cell++)
            {
                var selected = draws.Where(d => d.Cell == cell).ToArray();
                Assert.Equal(perCell, selected.Length);
                for (int i = 0; i < selected.Length; i++)
                {
                    var draw = selected[i];
                    var r = scene.Actors[i / 2].Frame.Preparation!.GpuReservation;
                    var range = scene.Actors[i / 2].Topology.MaterialRanges[i % 2];
                    Assert.Same(batch.Current, draw.Current); Assert.Same(batch.StaticData, draw.Static);
                    Assert.Equal((uint)i * 256, draw.DynamicOffset);
                    if (pass == "cascade") Assert.Equal((uint)(cell * 20 + i) * 256, draw.PassOffset);
                    else Assert.Equal((uint)cell * 256, draw.PassOffset % (6 * 256));
                    Assert.Equal(new[] { r.CurrentBase, r.StaticBase, (uint)range.FirstIndex, r.PreviousBase }, draw.Words);
                    Assert.Equal((uint)range.IndexCount, draw.Corners);
                    Assert.Equal(1u, draw.Instances); Assert.Equal(0u, draw.FirstVertex); Assert.Equal(0u, draw.FirstInstance);
                    if (i % 2 == 1) Assert.True(range.FirstIndex > 0);
                }
            }
        }
        Assert.DoesNotContain(scene.Bindings.Draws, d => d.Pass == "main");
        Assert.Equal(0, scene.Scene.OrdinaryCpuSkinnedVerticesForTests);
        Assert.Equal(0, scene.Scene.CoverageDiagnosticsForTests.UncoveredGpuDraws);
        Assert.Equal(10, scene.Scene.DispatchedHostPreparationCount);
        string stem = $"shadow-static{staticLight}-refused{refusal}-effects{effects}";
        string path = "/tmp/grimhollow-equipment-coverage-task11";
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, stem + ".bindings.json"), scene.Bindings.Json());
        File.WriteAllBytes(Path.Combine(path, stem + ".cascade.f32"), MemoryMarshal.AsBytes(actual.Cascades.Values.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(path, stem + ".cascade-reference.f32"), MemoryMarshal.AsBytes(expected.Cascades.Values.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(path, stem + ".point.f32"), MemoryMarshal.AsBytes(actual.Points.Values.AsSpan()).ToArray());
        File.WriteAllBytes(Path.Combine(path, stem + ".point-reference.f32"), MemoryMarshal.AsBytes(expected.Points.Values.AsSpan()).ToArray());
        using (var buffers = File.Create(Path.Combine(path, stem + ".selected-gpu.u32")))
            foreach (var actor in scene.Actors)
            {
                var r = actor.Frame.Preparation!.GpuReservation;
                buffers.Write(MemoryMarshal.AsBytes(words.AsSpan((int)r.CurrentBase, 16)));
                int start = checked((int)r.CurrentBase + 16 + (refusal ? 0 : r.SourceCount * 16));
                buffers.Write(MemoryMarshal.AsBytes(words.AsSpan(start, (refusal ? r.SourceCount : r.VertexCount) * 16)));
            }
        var differences = Enumerable.Range(0, actual.Cascades.Values.Length)
            .Where(i => BitConverter.SingleToUInt32Bits(actual.Cascades.Values[i]) != BitConverter.SingleToUInt32Bits(expected.Cascades.Values[i]))
            .Take(4).Select(i => new { Index = i, Actual = BitConverter.SingleToUInt32Bits(actual.Cascades.Values[i]),
                OrdinaryReference = BitConverter.SingleToUInt32Bits(expected.Cascades.Values[i]) }).ToArray();
        File.WriteAllText(Path.Combine(path, stem + ".metrics.json"), JsonSerializer.Serialize(new
        {
            Backend = fixture.Device.Backend.ToString(), Device = fixture.Device.Capabilities.DeviceName,
            ComparedCascades = 4, ComparedPointFaces = 6, ShadowSurfaceOffsetMismatches = 0,
            UncoveredGhostEnvelopePixels = 0, MissingShadowPixels = 0, CoveredCpuVertices = scene.Scene.OrdinaryCpuSkinnedVerticesForTests,
            MainDraws = 0, NumericalRefusal = refusal, FinalWordMismatches = 0,
            OrdinaryCascadeWordDifferences = differences,
            Actors = scene.Actors.Select(a => new { a.Key.Value, a.Frame.Preparation!.Sphere.Radius,
                Center = a.Frame.Preparation!.Sphere.Center.ToString() }).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (refusal)
        {
            // Actual GPU words, refusal and native bindings have already been checked and saved above.
            using var control = scene.CreateOriginalPoseControl();
            var fallback = scene.CaptureShadows(true, staticLight, shadowOnly: true, cameraCulled: true, control);
            File.WriteAllBytes(Path.Combine(path, stem + ".fallback-control-cascade.f32"), MemoryMarshal.AsBytes(fallback.Cascades.Values.AsSpan()).ToArray());
            File.WriteAllBytes(Path.Combine(path, stem + ".fallback-control-point.f32"), MemoryMarshal.AsBytes(fallback.Points.Values.AsSpan()).ToArray());
            Assert.Equal(MemoryMarshal.Cast<float, uint>(fallback.Cascades.Values).ToArray(), MemoryMarshal.Cast<float, uint>(actual.Cascades.Values).ToArray());
            Assert.Equal(MemoryMarshal.Cast<float, uint>(fallback.Points.Values).ToArray(), MemoryMarshal.Cast<float, uint>(actual.Points.Values).ToArray());
        }
        output.WriteLine($"{fixture.Device.Backend} {fixture.Device.Capabilities.DeviceName}: cascades=4 faces=6 actors=10 materials=2 static={staticLight} refusal={refusal} effects={effects}. Actual bound surface/offset mismatches=0, final word mismatches=0, shadow-only main draws=0.");
    }

    static void AssertTiles(CoverageTargetReadback actual, CoverageTargetReadback reference, int cells)
    {
        int size = actual.Width / cells;
        for (int cell = 0; cell < cells; cell++)
        {
            int pixels = 0, ghosts = 0, holes = 0;
            for (int y = 0; y < actual.Height; y++)
                for (int x = cell * size; x < (cell + 1) * size; x++)
                {
                    int i = y * actual.Width + x;
                    if (actual.Values[i] < .999f) pixels++;
                    if (actual.Values[i] < .999f && reference.Values[i] >= .999f) ghosts++;
                    if (actual.Values[i] >= .999f && reference.Values[i] < .999f) holes++;
                }
            Assert.True(pixels > 0, $"Empty shadow cell {cell}");
            Assert.Equal(0, ghosts);
            Assert.Equal(0, holes);
        }
    }

    [GpuFact]
    public void Shared_shadow_scene_matches_fresh_after_light_and_surface_changes()
    {
        var scene = fixture.Scene;
        scene.Select(true, false); _ = scene.CaptureShadows(true, true, false, true);
        scene.Select(false, true); _ = scene.CaptureShadows(true, false, true, false);
        scene.Select(false, false);
        var actual = scene.CaptureShadows(true, false, false, true);
        using var fresh = new CoveragePullingTestScene(fixture.Device, 10);
        var expected = fresh.CaptureShadows(true, false, false, true);
        CoveragePixelProof.EqualTarget(actual.Cascades, expected.Cascades, 0, "fresh cascades");
        CoveragePixelProof.EqualTarget(actual.Points, expected.Points, 0, "fresh point faces");
    }
}

public sealed class CoverageShadowPullingFixture : IDisposable
{
    GpuDeviceContext? gpu;
    CoveragePullingTestScene? scene;
    internal IGpuDevice Device => (gpu ??= GpuDeviceContext.CreateHeadless()).GpuDevice;
    internal CoveragePullingTestScene Scene => scene ??= new(Device, 10);
    public void Dispose() { scene?.Dispose(); gpu?.Dispose(); }
}
