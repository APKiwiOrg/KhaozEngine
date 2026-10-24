using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class FoliagePointCasterIndexTests(ITestOutputHelper output)
{
    static readonly MeshBounds Bounds = new(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f));
    static readonly MeshHandle Mesh = new(1, 1);

    [Fact]
    public void Sparse_query_tests_only_nearby_proxies()
    {
        var items = new FoliageInstance[2048];
        for (int i = 0; i < items.Length; i++) items[i] = Caster(64f * i);
        using FoliagePointShadowStream first = Build(items);
        for (int i = 0; i < items.Length; i++) items[i] = Caster(64f * (2048 + i));
        using FoliagePointShadowStream second = Build(items);
        Source[] sources = [new(0, first, [new(0, 2048)]), new(1, second, [new(0, 2048)])];
        var index = Index(sources);
        var actual = new List<FoliagePointCasterRef>();
        var light = new Shell(Vector3.Zero, 2f);
        List<FoliagePointCasterRef> expected = FullScan(sources, light, out int oracleTouchTests);
        Query(index, light, actual);
        Assert.Equal(new[] { new FoliagePointCasterRef(0, 0) }, actual);
        Assert.Equal(expected, actual);
        Assert.Equal(4096, oracleTouchTests);
        output.WriteLine($"Sparse exact tests: oracle={oracleTouchTests}, index={index.TouchTests}");
        Assert.InRange(index.TouchTests, 1, 16);
    }

    [Fact]
    public void Query_preserves_source_and_supplied_range_order()
    {
        using FoliagePointShadowStream first = Build([Caster(0), Caster(1), Caster(2), Caster(3), Caster(4)]);
        using FoliagePointShadowStream second = Build([Caster(0), Caster(1)]);
        Source[] sources = [new(0, first, [new(3, 2), new(0, 1)]), new(1, second, [new(0, 2)])];
        List<FoliagePointCasterRef> actual = AssertOracle(sources, new Shell(Vector3.Zero, 10f));
        Assert.Equal(new FoliagePointCasterRef[] { new(0, 3), new(0, 4), new(0, 0), new(1, 0), new(1, 1) }, actual);
        Assert.Equal(new[] { new FaceRun(0, Mesh, 3, 2), new FaceRun(0, Mesh, 0, 1), new FaceRun(1, Mesh, 0, 2) },
            FaceRuns(sources, actual));
    }

    [Fact]
    public void Query_keeps_layout_gaps_mesh_generations_and_integer_range_endpoints()
    {
        var nextGeneration = new MeshHandle(Mesh.Index, Mesh.Generation + 1);
        using FoliagePointShadowStream stream = Build(
        [
            Caster(0), Caster(1) with { CastsShadows = false }, Caster(2),
            Caster(3) with { Mesh = nextGeneration }, Caster(4),
            Caster(5) with { Mesh = new MeshHandle(2, 1) },
        ]);
        FoliagePointShadowRange[][] ranges =
        [
            [new(0, 6)], [new(0, 1), new(3, 2)], [new(3, 2), new(0, 1)], [],
            [new(2, 0)], [new(4, -2)], [new(-2, 4)], [new(int.MinValue, int.MaxValue)],
            [new(2, int.MaxValue)], [new(int.MaxValue, int.MaxValue)], [new(int.MinValue, int.MinValue)],
        ];
        foreach (FoliagePointShadowRange[] selection in ranges)
            AssertOracle([new Source(0, stream, selection)], new Shell(Vector3.Zero, 10f));
        var all = new Source[] { new(0, stream, [new(0, 6)]) };
        Assert.Equal(new[] { new FaceRun(0, Mesh, 0, 2), new FaceRun(0, nextGeneration, 2, 1),
            new FaceRun(0, Mesh, 3, 1), new FaceRun(0, new MeshHandle(2, 1), 4, 1) },
            FaceRuns(all, AssertOracle(all, new Shell(Vector3.Zero, 10f))));
    }

    [Fact]
    public void Query_matches_full_scan_at_shell_and_index_boundaries()
    {
        FoliageInstance[] boundaryItems =
        [
            Caster(3), Caster(MathF.BitIncrement(3f)), Caster(MathF.BitDecrement(3f)),
            Caster(0), Caster(MathF.BitIncrement(1f)), Caster(MathF.BitDecrement(1f)),
            Caster(8), Caster(MathF.BitIncrement(8f)), Caster(MathF.BitDecrement(8f)),
            Caster(-8), Caster(MathF.BitIncrement(-8f)), Caster(MathF.BitDecrement(-8f)),
            Caster(0) with { Transform = Matrix4x4.CreateScale(12f) * Matrix4x4.CreateTranslation(13f, 0f, 0f) },
            Caster(float.NaN), Caster(float.PositiveInfinity), Caster(float.NegativeInfinity),
            Caster(0) with { Transform = Matrix4x4.CreateScale(float.NaN) },
            Caster(0) with { Transform = Matrix4x4.CreateScale(float.PositiveInfinity) }, Caster(1e9f),
        ];
        // Keep enough binned proxies to use cell selection for finite small shells instead of the full-walk fallback.
        var items = new FoliageInstance[boundaryItems.Length + 128];
        boundaryItems.CopyTo(items, 0);
        for (int i = boundaryItems.Length; i < items.Length; i++) items[i] = Caster(64f * i);
        using FoliagePointShadowStream stream = Build(items);
        Source[] sources = [new(0, stream, [new(0, int.MaxValue)])];
        Shell[] lights =
        [
            new(Vector3.Zero, 2f), new(Vector3.Zero, 20f, 1f),
            new(Vector3.Zero, 20f, MathF.BitIncrement(1f)),
            new(Vector3.Zero, 20f, 0f, new(-1f), new(1f)),
            new(Vector3.Zero, 20f, 0f, new(MathF.BitDecrement(-1f)), new(MathF.BitIncrement(1f))),
            new(new(1e9f, 0f, 0f), 2f), new(Vector3.Zero, float.PositiveInfinity),
            new(new(float.NaN, 0f, 0f), 2f), new(Vector3.Zero, float.NaN),
        ];
        foreach (Shell light in lights) AssertOracle(sources, light);
    }

    [Fact]
    public void Fixed_seed_queries_match_the_selected_full_scan()
    {
        var random = new Random(5741);
        var streams = new FoliagePointShadowStream[3];
        var sources = new Source[3];
        try
        {
            for (int s = 0; s < streams.Length; s++)
            {
                var items = new FoliageInstance[160];
                for (int i = 0; i < items.Length; i++)
                    items[i] = new FoliageInstance(new MeshHandle(i % 3, i % 2 + 1),
                        Matrix4x4.CreateScale(i % 19 == 0 ? 12f : .5f) * Matrix4x4.CreateTranslation(
                            random.Next(-100, 101), random.Next(-10, 11), random.Next(-100, 101)),
                        .1f, CastsShadows: i % 7 != 0);
                streams[s] = Build(items);
                sources[s] = new Source(s, streams[s], [new(90, 60), new(0, 50), new(65, 10)]);
            }
            var index = Index(sources);
            var actual = new List<FoliagePointCasterRef>();
            for (int q = 0; q < 64; q++)
            {
                var light = new Shell(new(random.Next(-100, 101), random.Next(-10, 11), random.Next(-100, 101)),
                    random.Next(1, 40), q % 3 == 0 ? 8f : 0f, new(-12f), new(12f));
                Query(index, light, actual);
                Assert.Equal(FullScan(sources, light, out _), actual);
                Assert.Equal(FullScanRuns(sources, light), FaceRuns(sources, actual));
            }
        }
        finally { foreach (FoliagePointShadowStream? stream in streams) stream?.Dispose(); }
    }

    [Fact]
    public void Empty_frame_removes_prior_membership()
    {
        using FoliagePointShadowStream stream = Build([Caster(0), Caster(1)]);
        var index = Index([new Source(0, stream, [new(0, 2)])]);
        var hits = new List<FoliagePointCasterRef>();
        Query(index, new Shell(Vector3.Zero, 2f), hits);
        Assert.Equal(2, hits.Count);
        int tests = index.TouchTests;
        Query(index, new Shell(Vector3.Zero, 2f), hits);
        Assert.Equal(tests * 2, index.TouchTests);
        index.Reset();
        index.Seal();
        Query(index, new Shell(Vector3.Zero, 2f), hits);
        Assert.Empty(hits);
        Assert.Equal(0, index.TouchTests);
        index.Reset();
        index.Add(2, stream, [new(1, 1)]);
        index.Seal();
        Query(index, new Shell(Vector3.Zero, 2f), hits);
        Assert.Equal(new[] { new FoliagePointCasterRef(2, 1) }, hits);
    }

    [Fact]
    public void Warmed_frame_build_and_query_allocate_zero_bytes()
    {
        var items = new FoliageInstance[128];
        for (int i = 0; i < items.Length; i++) items[i] = Caster(i * 64f);
        using FoliagePointShadowStream stream = Build(items);
        FoliagePointShadowRange[] ranges = [new(0, items.Length)];
        var index = new FoliagePointCasterIndex();
        var hits = new List<FoliagePointCasterRef>();
        void Frame()
        {
            index.Reset();
            index.Add(0, stream, ranges);
            index.Add(1, stream, ranges);
            index.Seal();
            index.Query(Vector3.Zero, 10000f, 0f, default, default, hits);
            index.Query(Vector3.Zero, 2f, 0f, default, default, hits);
        }
        for (int i = 0; i < 4; i++) Frame();
        long allocatedBytes;
        using (AllocationRegion.Start("Foliage point index frame build and query"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Frame();
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocatedBytes);
        Assert.Equal(new FoliagePointCasterRef[] { new(0, 0), new(1, 0) }, hits);
        output.WriteLine($"Warmed frame allocated bytes: {allocatedBytes}");
    }

    static FoliageInstance Caster(float x) =>
        new(Mesh, Matrix4x4.CreateTranslation(x, 0f, 0f), .1f, CastsShadows: true);
    static FoliagePointShadowStream Build(FoliageInstance[] items) => FoliagePointShadowStream.Build(items, _ => Bounds);
    readonly record struct Source(int Ordinal, FoliagePointShadowStream Stream, FoliagePointShadowRange[] Ranges);
    readonly record struct Shell(Vector3 Position, float Radius, float Near = 0f, Vector3 Min = default, Vector3 Max = default);
    readonly record struct FaceRun(int SourceOrdinal, MeshHandle Mesh, uint Start, uint Count);

    static FoliagePointCasterIndex Index(Source[] sources)
    {
        var index = new FoliagePointCasterIndex();
        index.Reset();
        foreach (Source source in sources) index.Add(source.Ordinal, source.Stream, source.Ranges);
        index.Seal();
        return index;
    }

    static void Query(FoliagePointCasterIndex index, Shell light, List<FoliagePointCasterRef> actual) =>
        index.Query(light.Position, light.Radius, light.Near, light.Min, light.Max, actual);

    static List<FoliagePointCasterRef> AssertOracle(Source[] sources, Shell light)
    {
        var actual = new List<FoliagePointCasterRef>();
        Query(Index(sources), light, actual);
        Assert.Equal(FullScan(sources, light, out _), actual);
        Assert.Equal(FullScanRuns(sources, light), FaceRuns(sources, actual));
        return actual;
    }

    // Walk original selected proxies directly. No adapter selection helper, cells or spatial candidate list.
    static List<FoliagePointCasterRef> FullScan(Source[] sources, Shell light, out int touchTests)
    {
        var result = new List<FoliagePointCasterRef>();
        touchTests = 0;
        foreach (Source source in sources)
            foreach (FoliagePointShadowRange range in source.Ranges)
                foreach (FoliagePointShadowCaster caster in source.Stream.Casters)
                {
                    if (caster.SourceIndex < range.Start || caster.SourceIndex >= (long)range.Start + range.Count) continue;
                    touchTests++;
                    var sphere = new PointShadowCasterSphere(new(caster.Sphere.X, caster.Sphere.Y, caster.Sphere.Z), caster.Sphere.W);
                    if (sphere.TouchesShadowingShell(light.Position, light.Radius, light.Near, light.Min, light.Max))
                        result.Add(new FoliagePointCasterRef(source.Ordinal, caster.Slot));
                }
        return result;
    }

    static List<FaceRun> FullScanRuns(Source[] sources, Shell light)
    {
        var result = new List<FaceRun>();
        var runs = new List<FoliagePointShadowRun>();
        foreach (Source source in sources)
        {
            source.Stream.Query(source.Ranges, light.Position, light.Radius, light.Near, light.Min, light.Max, runs);
            foreach (FoliagePointShadowRun run in runs) result.Add(new FaceRun(source.Ordinal, run.Mesh, run.Start, run.Count));
        }
        return result;
    }

    static List<FaceRun> FaceRuns(Source[] sources, List<FoliagePointCasterRef> hits)
    {
        var result = new List<FaceRun>();
        foreach (FoliagePointCasterRef hit in hits)
        {
            Source source = Array.Find(sources, s => s.Ordinal == hit.SourceOrdinal);
            FoliagePointShadowCaster caster = source.Stream.Casters[(int)hit.ProxySlot];
            if (result.Count > 0 && result[^1] is var last && last.SourceOrdinal == hit.SourceOrdinal
                && last.Mesh.Equals(caster.Mesh) && last.Start + last.Count == hit.ProxySlot)
                result[^1] = last with { Count = last.Count + 1 };
            else result.Add(new FaceRun(hit.SourceOrdinal, caster.Mesh, hit.ProxySlot, 1));
        }
        return result;
    }
}
