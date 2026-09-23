using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="PointCasterIndex"/> answers every light exactly as the walk over every caster does, slot for slot and
/// in slot order, while testing only the casters near the light. The oracle is <see cref="PointCasterFullWalk"/>.
/// Seeded, so a failure names a seed that reproduces it.
/// </summary>
[Collection("AllocSensitive")]   // one case is a zero-allocation reading (#264)
public sealed class PointCasterIndexTests
{
    [Fact]
    public void TheSphereOverloadAnswersExactlyAsTheBoundsOverload()
    {
        var random = new Random(1110);
        var bounds = new MeshBounds(new Vector3(-0.5f, 0f, -0.25f), new Vector3(0.5f, 2f, 0.25f));
        for (int i = 0; i < 2_000; i++)
        {
            Matrix4x4 model = Matrix4x4.CreateScale(Range(random, 0.2f, 4f))
                * Matrix4x4.CreateRotationY(Range(random, 0f, MathF.Tau))
                * Matrix4x4.CreateTranslation(RandomPoint(random, 30f));
            Vector3 light = RandomPoint(random, 30f);
            float radius = Range(random, 0.5f, 20f);
            float near = random.Next(3) == 0 ? Range(random, 0f, 3f) : 0f;
            bool boxed = random.Next(2) == 0;
            Vector3 min = boxed ? light - new Vector3(Range(random, 0f, 4f)) : default;
            Vector3 max = boxed ? light + new Vector3(Range(random, 0f, 4f)) : default;
            bounds.WorldSphere(model, out Vector3 centre, out float sphereRadius);

            Assert.Equal(
                Scene3D.InstanceTouchesLight(bounds, model, light, radius, near, min, max),
                Scene3D.InstanceTouchesLight(centre, sphereRadius, light, radius, near, min, max));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryQueryReturnsTheFullWalksSlotsInSlotOrder(int seed)
    {
        var random = new Random(seed);
        var index = new PointCasterIndex();
        var hits = new List<int>();
        int queries = 0, touched = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            // Frame 0 holds nothing, so an empty frame is asked too. Later frames shrink and grow, so the reused
            // arrays carry stale entries past the live count, and a query that read one would fail here.
            List<PointCasterFullWalk.Caster> casters = frame == 0
                ? new List<PointCasterFullWalk.Caster>()
                : RandomCasters(random, random.Next(1, 400));
            index.Reset(casters.Count == 0 ? 0 : casters[^1].Slot + 1);
            foreach (PointCasterFullWalk.Caster caster in casters)
                index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
            index.Seal();

            for (int q = 0; q < 32; q++)
            {
                (Vector3 at, float radius, float near, Vector3 min, Vector3 max) = RandomLight(random);
                List<int> expected = PointCasterFullWalk.Slots(casters, at, radius, near, min, max);
                index.Query(at, radius, near, min, max, hits);
                Assert.Equal(expected, hits);
                queries++;
                if (expected.Count > 0) touched++;
            }
        }
        // Two empty lists agreeing proves nothing, so a seed whose lights all miss is not a pass.
        Assert.True(touched > queries / 8, $"only {touched} of {queries} queries touched a caster");
    }

    [Fact]
    public void ACasterTheFloatTestKeepsJustPastTheGrownSphereIsStillFound()
    {
        // The light's sphere grown by one cell ends exactly on the x = 0 cell edge. The caster's centre is a hair
        // below it, in cell -1, and at this magnitude the hair rounds away, so the exact test sees a distance of
        // exactly its reach and keeps the caster. A query that visited only the grown sphere's own cells would
        // start at cell 0 and lose it, which is what QuerySlack is for.
        var light = new Vector3(16f, 0f, 0f);
        var casters = new List<PointCasterFullWalk.Caster>
        {
            new(0, new Vector3(-1e-7f, 0f, 0f), PointCasterIndex.CellSize),
        };
        // Far casters the light never reaches, enough that the query walks its columns rather than falling back to
        // the whole grid, which would find the caster above with or without the slack.
        for (int slot = 1; slot <= 64; slot++)
            casters.Add(new(slot, new Vector3(500f, 0f, 0f), 1f));
        var index = new PointCasterIndex();
        index.Reset(casters.Count);
        foreach (PointCasterFullWalk.Caster caster in casters)
            index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
        index.Seal();
        var hits = new List<int>();

        index.Query(light, 8f, 0f, default, default, hits);

        Assert.Equal(new[] { 0 }, PointCasterFullWalk.Slots(casters, light, 8f, 0f, default, default));
        Assert.Equal(new[] { 0 }, hits);
    }

    [Fact]
    public void NonFiniteAndFarCastersAreAnsweredAsTheFullWalkAnswersThem()
    {
        // A NaN centre or an infinite radius touches every light under the shared test, because each comparison
        // that would reject it is false. A centre past the packed cell range cannot be binned. All three are
        // always candidates, and a light radius the queue would never admit walks the whole grid.
        var casters = new List<PointCasterFullWalk.Caster>
        {
            new(0, new Vector3(float.NaN, 0f, 0f), 1f),
            new(1, new Vector3(3f, 0f, 0f), float.PositiveInfinity),
            new(2, new Vector3(1e12f, 0f, 0f), 1f),
            new(3, new Vector3(2f, 0f, 0f), 1f),
        };
        var index = new PointCasterIndex();
        index.Reset(4);
        foreach (PointCasterFullWalk.Caster caster in casters)
            index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
        index.Seal();
        var hits = new List<int>();

        Assert.Equal(3, index.OversizeCount);
        foreach ((Vector3 light, float radius) in new[]
        {
            (Vector3.Zero, 6f), (new Vector3(500f, 0f, 0f), 6f), (new Vector3(1e12f, 0f, 0f), 6f),
            (Vector3.Zero, -20f), (Vector3.Zero, float.NaN),
        })
        {
            index.Query(light, radius, 0f, default, default, hits);
            Assert.Equal(PointCasterFullWalk.Slots(casters, light, radius, 0f, default, default), hits);
        }
    }

    [Fact]
    public void TouchTestsFollowTheCastersNearEachLightRatherThanLightsTimesCasters()
    {
        // A sparse town from above: 4,096 unit casters on a 20 m lattice, 1.26 km on a side.
        const int side = 64;
        const float spacing = 20f;
        const int lights = 32;
        const float radius = 10f;
        var index = new PointCasterIndex();
        index.Reset(side * side);
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                index.Add(x * side + z, 0, new Vector3(x * spacing, 0.5f, z * spacing), 0.87f);
        index.Seal();

        var random = new Random(1110);
        var hits = new List<int>();
        int kept = 0;
        for (int i = 0; i < lights; i++)
        {
            // Over a lattice point, so every light has at least the caster beneath it.
            var light = new Vector3(random.Next(side) * spacing, 3f, random.Next(side) * spacing);
            index.Query(light, radius, 0f, default, default, hits);
            Assert.NotEmpty(hits);
            kept += hits.Count;
        }

        // A query visits the cells under its light grown by radius, one cell and the slack, 18.25 m, and the cell
        // floor adds under one more cell on each side, so its window is under 52.5 m per horizontal axis. Casters
        // 20 m apart put at most three columns in that, so no light tests more than nine. The walk this replaced
        // tested all 4,096 for every light.
        Assert.InRange(index.TouchTests, kept, 9 * lights);
        Assert.True(index.TouchTests < side * side,
            $"{lights} lights ran {index.TouchTests} touch tests, more than one full walk of {side * side} casters");
    }

    [Fact]
    public void AWarmRebuildAndItsQueriesAllocateNothing()
    {
        List<PointCasterFullWalk.Caster> casters = RandomCasters(new Random(7), 300);
        var index = new PointCasterIndex();
        var hits = new List<int>();
        void Frame()
        {
            index.Reset(casters[^1].Slot + 1);
            foreach (PointCasterFullWalk.Caster caster in casters)
                index.Add(caster.Slot, 0, caster.Centre, caster.Radius);
            index.Seal();
            for (int x = -96; x <= 96; x += 32)
                index.Query(new Vector3(x, 0f, 0f), 20f, 0f, default, default, hits);
        }

        for (int i = 0; i < 4; i++) Frame();   // warm every grow-only array and the sort helpers
        AllocAssert.NoPerCallAllocation("PointCasterIndex rebuild and queries", () =>
        {
            for (int i = 0; i < 10; i++) Frame();
        });
    }

    static List<PointCasterFullWalk.Caster> RandomCasters(Random random, int count)
    {
        var casters = new List<PointCasterFullWalk.Caster>(count);
        int slot = -1;
        for (int i = 0; i < count; i++)
        {
            // Skipped slots stand for the instances the scene never adds: opted out, a stale handle, terrain.
            slot += random.Next(3) == 0 ? 2 : 1;
            Vector3 centre = RandomPoint(random, 96f);
            if (random.Next(6) == 0) centre = Snap(centre);   // on a cell corner, where a floor is easiest to get wrong
            float radius = random.Next(10) switch
            {
                0 => Range(random, 8.5f, 40f),      // oversize: a ground chunk, a cliff
                1 => PointCasterIndex.CellSize,      // the widest caster the grid still bins
                _ => Range(random, 0.05f, 4f),
            };
            casters.Add(new PointCasterFullWalk.Caster(slot, centre, radius));
        }
        return casters;
    }

    static (Vector3 At, float Radius, float Near, Vector3 Min, Vector3 Max) RandomLight(Random random)
    {
        float radius = Range(random, 1f, 24f);
        Vector3 at = RandomPoint(random, 100f);
        // One in three stands where its sphere grown by a cell ends on a cell edge on every axis.
        if (random.Next(3) == 0) at = Snap(at) + new Vector3(radius + PointCasterIndex.CellSize);
        float near = random.Next(4) == 0 ? Range(random, 0f, radius * 0.5f) : 0f;
        bool boxed = random.Next(4) == 0;
        var half = new Vector3(Range(random, 0.1f, 3f), Range(random, 0.1f, 3f), Range(random, 0.1f, 3f));
        return (at, radius, near, boxed ? at - half : default, boxed ? at + half : default);
    }

    static Vector3 RandomPoint(Random random, float extent) => new(
        Range(random, -extent, extent), Range(random, -extent * 0.25f, extent * 0.25f), Range(random, -extent, extent));

    static Vector3 Snap(Vector3 p) => new(
        MathF.Round(p.X / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Y / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Z / PointCasterIndex.CellSize) * PointCasterIndex.CellSize);

    static float Range(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);
}
