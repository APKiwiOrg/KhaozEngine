using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void WarmMeshFindCapturesNoTriangles()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        using IPhysicsQueryLease lease = scene.AcquireLease();
        SupportQueryDiagnostics.Reset();
        scene.Find(lease);
        SupportQueryCounts cold = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(2, cold.WorldFormBuilds);
        Assert.Equal(2, cold.MeshTriangleCaptures);
        SupportQueryDiagnostics.Reset();
        scene.Find(lease);
        SupportQueryCounts warm = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(0, warm.MeshTriangleCaptures);
        Assert.Equal(cold with { WorldFormBuilds = 0, MeshTriangleCaptures = 0 }, warm);
        long bytes = AllocationMeasurement.Measure(nameof(WarmMeshFindCapturesNoTriangles), () => scene.Find(lease));
        Assert.Equal(0, bytes);
        SupportQueryDiagnostics.Reset();
        using (SupportReferenceEvaluation.Begin()) scene.Find(lease);
        Assert.Equal(2, SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
    }

    [Theory]
    [InlineData(0.1f, 0.2f)]
    [InlineData(-0.1f, 0.2f)]
    public void WarmMeshFindAcrossACoplanarNeighbourAllocatesNothing(float x, float z)
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        using IPhysicsQueryLease lease = scene.AcquireLease();
        var query = new FootSupportQuery(new(x, z), 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        SupportSample expected = SupportFootprintEquivalence.AssertMatchesReference(
            () => FootSupport.Find(null, scene.Target, lease, query));
        FootSupport.Find(null, scene.Target, lease, query);
        SupportQueryDiagnostics.Reset();
        SupportSample warm = default;
        long bytes = AllocationMeasurement.Measure(nameof(WarmMeshFindAcrossACoplanarNeighbourAllocatesNothing), () =>
            warm = FootSupport.Find(null, scene.Target, lease, query));
        Assert.Equal(expected, warm);
        Assert.Equal(0, bytes);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
    }

    [Fact]
    public void MeshWorldFormMatchesReferenceOnEveryCostScene()
    {
        using var counters = new WorldFormCounters();
        long meshBuilds = 0;
        foreach (var row in CostRows())
        {
            using var scene = new WorldFormScene(row);
            scene.Checked($"{row.Name} cold", out long builds);
            if (row.Name == "flat mesh") meshBuilds += builds;
            scene.Checked($"{row.Name} warm", out _);
            scene.CheckedFootprint($"{row.Name} raw");
            if (row.Name.StartsWith("flat ", StringComparison.Ordinal) ||
                row.Name.StartsWith("stairs ", StringComparison.Ordinal))
                scene.CheckedSubstep($"{row.Name} substep");
        }
        Assert.Equal(2, meshBuilds);
    }

    [Fact]
    public void ChangedMeshPoseRebuildsTheForm()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        var before = scene.Checked("mesh before", out _);
        scene.World.Rebase(new(0, 0.125f, 0));
        var after = scene.Checked("mesh moved", out long builds);
        Assert.Equal(2, builds);
        Assert.Equal(before.Height - 0.125f, after.Height);
        scene.Checked("mesh moved warm", out builds);
        Assert.Equal(0, builds);
    }

    [Fact]
    public void MeshWorldFormStaysWithinCapacity()
    {
        using var counters = new WorldFormCounters();
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        int patches = InstalledSupportMeshWorldCache.PerStaticTriangleCapacity / 2 + 1;
        var vertices = new Vector3[patches * 4];
        var indices = new int[patches * 6];
        for (int patch = 0; patch < patches; patch++)
        {
            vertices[patch * 4] = new(patch - 0.3f, 0, -0.3f);
            vertices[patch * 4 + 1] = new(patch - 0.3f, 0, 0.3f);
            vertices[patch * 4 + 2] = new(patch + 0.3f, 0, 0.3f);
            vertices[patch * 4 + 3] = new(patch + 0.3f, 0, -0.3f);
            new[] { 0, 2, 1, 0, 3, 2 }.Select(i => i + patch * 4).ToArray().CopyTo(indices, patch * 6);
        }
        int staticCount = InstalledSupportMeshWorldCache.GlobalTriangleCapacity /
            InstalledSupportMeshWorldCache.PerStaticTriangleCapacity + 1;
        StaticHandle[] owners = [.. Enumerable.Range(0, staticCount)
            .Select(slot => world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.At(new(0, 0, slot * 4))))];
        InstalledSupportMeshWorldCache cache = MeshForms(world);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        int maximum = 0;
        for (int slot = 0; slot < owners.Length; slot++)
        {
            for (int patch = 0; patch < patches; patch++)
            {
                MeshFind(world, lease, new(patch, slot * 4));
                Assert.InRange(cache.TriangleCountFor(owners[slot]), 1,
                    InstalledSupportMeshWorldCache.PerStaticTriangleCapacity);
                maximum = Math.Max(maximum, cache.TriangleCount);
                Assert.InRange(cache.TriangleCount, 1, InstalledSupportMeshWorldCache.GlobalTriangleCapacity);
            }
            Assert.Equal(InstalledSupportMeshWorldCache.PerStaticTriangleCapacity, cache.TriangleCountFor(owners[slot]));
            SupportQueryDiagnostics.Reset();
            MeshFind(world, lease, new(0, slot * 4));
            // The first two triangles were evicted, and the reference independently captures both again.
            Assert.Equal(2, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
            Assert.Equal(4, SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
        }
        Assert.Equal(InstalledSupportMeshWorldCache.GlobalTriangleCapacity, maximum);
        Assert.Equal(0, cache.TriangleCountFor(owners[0]));
        MeshFind(world, lease, Vector2.Zero);
        Assert.Equal(2, cache.TriangleCountFor(owners[0]));
    }

    [Fact]
    public void ManySmallMeshStaticsStayWarmWithinTheGlobalTriangleBudget()
    {
        using var counters = new WorldFormCounters();
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var triangle = new TriangleMeshShape([new(-2, 0, -2), new(2, 0, -2), new(0, 0, 2)], [0, 1, 2]);
        for (int i = 0; i < 32; i++) world.AddStatic(triangle, Pose.Identity);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        MeshFind(world, lease, Vector2.Zero);
        Assert.Equal(32, MeshForms(world).TriangleCount);
        Assert.Equal(32, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        SupportQueryDiagnostics.Reset();
        FootSupport.Find(null, world, lease, CostRows().Single(row => row.Name == "flat mesh").Query);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
    }

    [Fact]
    public void OversizedMeshQueryUsesScratchWithoutChurningTheCache()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "grid 100"));
        using IPhysicsQueryLease lease = scene.AcquireLease();
        SupportQueryDiagnostics.Reset();
        SupportSample cold = scene.Find(lease);
        SupportQueryCounts coldCounts = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(SupportStatus.Refused, cold.Status);
        Assert.Equal(20000, coldCounts.MeshTriangleCaptures);
        Assert.Equal(0, coldCounts.WorldFormBuilds);
        Assert.Equal(0, MeshForms(scene.World).TriangleCount);
        SupportQueryDiagnostics.Reset();
        Assert.Equal(cold, scene.Find(lease));
        Assert.Equal(coldCounts, SupportQueryDiagnostics.Snapshot());
        Assert.Equal(0, MeshForms(scene.World).TriangleCount);
    }

    [Fact]
    public void CoplanarNeighbourCertificateMatchesKernel()
    {
        var kernel = new SupportSegmentPolygon();
        int positive = 0, negative = 0, fallback = 0;
        foreach (Vector3 centre in new[] { Vector3.Zero, LiftedCentre })
        {
            SupportPolygon polygon = Installed([new(-0.75f, 0.5f, -0.5f), new(0.625f, 0.5f, -0.25f),
                new(-0.125f, 0.5f, 0.875f)], centre);
            double y = polygon.Vertices[0].Y.Value;
            foreach ((_, float x, float z) in BoundarySamples(polygon))
                foreach (double height in new[] { 0.125, 0.25, 0.2501f })
                {
                    FeaturePoint start = Represented(x, y + height, z);
                    FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
                    GeometrySign actual = SupportPlanarNeighbour.Contact(polygon, start, direction, 0.25f, 0.0001f);
                    bool expected = ExhaustiveContact(kernel, polygon, start, direction, 0.25f);
                    if (actual == GeometrySign.Unresolved) fallback++;
                    else
                    {
                        Assert.Equal(expected, actual == GeometrySign.Positive);
                        positive += actual == GeometrySign.Positive ? 1 : 0;
                        negative += actual == GeometrySign.Negative ? 1 : 0;
                    }
                }
            // Sweep edge-interior and vertex disc tangencies, with represented steps on each side.
            foreach (double x in new[] { -1d, -0.8, -0.75, -0.5, 0, 0.5, 0.75, 1 })
                foreach (double z in new[] { -1d, -0.5, 0, 0.5, 1 })
                {
                    FeaturePoint start = Represented(x, y + 0.25, z);
                    FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
                    GeometrySign actual = SupportPlanarNeighbour.Contact(polygon, start, direction, 0.25f, 0.0001f);
                    if (actual == GeometrySign.Unresolved) continue;
                    Assert.Equal(ExhaustiveContact(kernel, polygon, start, direction, 0.25f), actual == GeometrySign.Positive);
                    negative += actual == GeometrySign.Negative ? 1 : 0;
                }
        }
        Assert.True(positive > 0 && negative > 0 && fallback > 0,
            $"{positive} positive, {negative} negative, {fallback} fallback.");

        SupportPolygon square = World([(-1, 0, -1), (1, 0, -1), (1, 0, 1), (-1, 0, 1)]);
        double reach = 0.25 + (double)0.0001f;
        foreach (double gap in new[] { 0d, 0.125, 0.25 })
        {
            double distance = Math.Sqrt(reach * reach - gap * gap);
            foreach ((double x, double z) in new[] { (1 + distance, 0d),
                (1 + distance / Math.Sqrt(2), 1 + distance / Math.Sqrt(2)) })
                foreach (double offset in new[] { Math.BitDecrement(x), x, Math.BitIncrement(x) })
                {
                    FeaturePoint start = Represented(offset, gap, z);
                    FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
                    GeometrySign verdict = SupportPlanarNeighbour.Contact(square, start, direction, 0.25f, 0.0001f);
                    if (verdict != GeometrySign.Unresolved)
                        Assert.Equal(ExhaustiveContact(kernel, square, start, direction, 0.25f),
                            verdict == GeometrySign.Positive);
                }
        }
        foreach (FeaturePoint direction in new[] { Represented(0.01, 0.01, 0), Represented(0, -0.01, 0),
            Represented(0, 0, 0), Represented(0, double.Epsilon, 0) })
            Assert.Equal(GeometrySign.Unresolved, SupportPlanarNeighbour.Contact(square, Represented(1.1, 0.125, 0),
                direction, 0.25f, 0.0001f));
        using (SupportReferenceEvaluation.Begin())
            Assert.Equal(GeometrySign.Unresolved, SupportPlanarNeighbour.Contact(square, Represented(1.1, 0.125, 0),
                Represented(0, 0.01f, 0), 0.25f, 0.0001f));
    }

    [Fact]
    public void MeshReferenceDoesNotPopulateOrBindTheFormAndPoseBitsRefuseReuse()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        InstalledSupportMeshWorldCache cache = MeshForms(scene.World);
        using (SupportReferenceEvaluation.Begin()) scene.Find();
        Assert.Equal(0, cache.TriangleCount);
        scene.Checked("mesh published", out long builds);
        Assert.Equal(2, builds);
        StaticHandle owner = scene.Owners().Single();
        object first = MeshForm(cache, owner, 0);
        scene.SetPose(owner, pose => pose with { Position = pose.Position with { X = -0f } });
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(scene.StaticPose(owner).Position.X));
        using (IPhysicsQueryLease lease = scene.AcquireLease())
        {
            SupportQueryDiagnostics.Reset();
            scene.Find(lease);
            Assert.Equal(2, SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        }
        scene.Checked("mesh signed zero", out builds);
        Assert.Equal(0, builds);
        Assert.Same(first, MeshForm(cache, owner, 0));
        scene.SetPose(owner, pose => pose with { Position = pose.Position with { Y = -0.125f } });
        SupportSample moved = scene.Checked("mesh skipped version writer", out builds);
        Assert.Equal(0, builds);
        Assert.True(moved.Height + moved.HeightError >= -0.125f && moved.Height - moved.HeightError <= -0.125f);
        Assert.Same(first, MeshForm(cache, owner, 0));
    }

    [Fact]
    public void ConcurrentMeshMissesPublishFirstWinsAndExactCounters()
    {
        const int readers = 3;
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        var results = new SupportSample[readers];
        var counts = new SupportQueryCounts[readers];
        using var barrier = new Barrier(readers);
        BoundedThread[] threads = [.. Enumerable.Range(0, readers).Select(slot => BoundedThread.Start(() =>
        {
            using var counters = new WorldFormCounters();
            using IPhysicsQueryLease lease = scene.AcquireLease();
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                // All readers finish building each cold triangle before any can publish it. Duplicate builds
                // are intentional when construction holds no shared lock, and only the first publication stays.
                if (phase == ContactAllocationPhase.SupportMaterialization) BoundedThread.Arrive(barrier);
            });
            try
            {
                SupportQueryDiagnostics.Reset();
                results[slot] = scene.Find(lease);
                counts[slot] = SupportQueryDiagnostics.Snapshot();
            }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
        }))];
        JoinMeshReaders(threads);
        Assert.All(counts, count => Assert.Equal(2, count.WorldFormBuilds));
        Assert.All(counts, count => Assert.Equal(2, count.MeshTriangleCaptures));
        Assert.Equal(2, MeshForms(scene.World).TriangleCount);
        SupportSample reference;
        using (SupportReferenceEvaluation.Begin()) reference = scene.Find();
        Assert.All(results, result => Assert.Equal(reference, result));
        using var serialCounters = new WorldFormCounters();
        using IPhysicsQueryLease serialLease = scene.AcquireLease();
        SupportQueryDiagnostics.Reset();
        scene.Find(serialLease);
        SupportQueryCounts serial = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(0, serial.MeshTriangleCaptures);
        Assert.Equal(0, serial.WorldFormBuilds);
        Assert.All(counts, c => Assert.Equal(serial, c with { WorldFormBuilds = 0, MeshTriangleCaptures = 0 }));
    }

    [Fact]
    public void ObservedMeshConstructionPublishesFirstWinsAndThrowPublishesNothing()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat mesh"));
        InstalledSupportMeshWorldCache cache = MeshForms(scene.World);
        StaticHandle owner = scene.Owners().Single();
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportMaterialization && entering)
                throw new InvalidOperationException("Mesh observer failure.");
        });
        try { Assert.Throws<InvalidOperationException>(() => scene.Find()); }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        Assert.Equal(0, cache.TriangleCount);
        SupportSample? nested = null;
        object? first = null;
        using (IPhysicsQueryLease lease = scene.AcquireLease())
        {
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                if (nested is not null || phase != ContactAllocationPhase.SupportMaterialization || !entering) return;
                nested = default(SupportSample);
                nested = scene.Find(lease);
                first = MeshForm(cache, owner, 0);
            });
            SupportSample outer;
            try { outer = scene.Find(lease); }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
            Assert.Equal(nested, outer);
        }
        Assert.NotNull(first);
        Assert.Same(first, MeshForm(cache, owner, 0));
        Assert.Equal(3, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        Assert.Equal(3, SupportQueryDiagnostics.Snapshot().MeshTriangleCaptures);
        scene.Checked("mesh warm after nested", out long builds);
        Assert.Equal(0, builds);
    }

    // Inspection stays in the test, without a production API for looking up cached triangle identities.
    static object MeshForm(InstalledSupportMeshWorldCache cache, StaticHandle owner, int id)
    {
        var slots = (IDictionary)cache.GetType().GetField("_statics", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;
        object slot = slots[owner.Value]!;
        var triangles = (IDictionary)slot.GetType().GetProperty("Triangles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slot)!;
        return triangles[id]!;
    }

    static InstalledSupportMeshWorldCache MeshForms(BepuPhysicsWorld world) =>
        ((InstalledSupportGeometryCache)typeof(BepuPhysicsWorld)
            .GetField("_installedSupportGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!)
        .MeshWorldForms;

    static void MeshFind(BepuPhysicsWorld world, IPhysicsQueryLease lease, Vector2 axis)
    {
        var query = new FootSupportQuery(axis, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        SupportSample sample = SupportFootprintEquivalence.AssertMatchesReference(
            () => FootSupport.Find(null, world, lease, query), $"mesh capacity {axis}");
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.True(sample.Height - sample.HeightError <= 0 && sample.Height + sample.HeightError >= 0);
    }
}
