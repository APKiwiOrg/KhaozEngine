using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class InstalledMeshSweepMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public void InstallationAndFirstSweepHaveCompactPerTriangleBudgets()
    {
        const int triangles = 4096;
        TriangleMeshShape shape = SparseMesh(triangles);
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle warm = world.AddStatic(shape, Pose.Identity);
        HitContains(Sweep(world), 1.25, 1.25);
        world.RemoveStatic(warm);

        StaticHandle installed = default;
        long installation = AllocationMeasurement.Measure("unswept mesh installation",
            () => installed = world.AddStatic(shape, Pose.Identity), 128 * 1024 * 1024);
        CapsuleSweepResult first = default;
        long sweep = AllocationMeasurement.Measure("first mesh sweep", () => first = Sweep(world), 128 * 1024 * 1024);
        double unswept = (double)installation / triangles, swept = (double)(installation + sweep) / triangles;
        output.WriteLine($"triangles={triangles}, unswept={unswept:F3} B/triangle, swept={swept:F3} B/triangle");
        HitContains(first, 1.25, 1.25);
        SameBits(first, Sweep(world));
        Assert.True(installed.Value > warm.Value);
        // At f23e19ec0, unswept/swept measured 5606.725/5606.725 B/triangle.
        // Compact capture measured 105.740/153.734 B/triangle on arm64 .NET 10.
        // Raw triangles, proved nodes and traversal order need 104 B/triangle, with
        // 48 more for lazy bounds. Budgets leave 24/40 B for fixed bookkeeping and runtimes.
        Assert.True(unswept < 128, $"Unswept mesh allocated {unswept:F3} B/triangle, budget 128.");
        Assert.True(swept < 192, $"Swept mesh allocated {swept:F3} B/triangle, budget 192.");
    }

    [Fact]
    public async Task ConcurrentFirstSweepsPublishTheSameCompleteResult()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(SparseMesh(2048), Pose.Identity);
        using var ready = new Barrier(3);
        Task<CapsuleSweepResult> a = Task.Run(ReadTogether);
        Task<CapsuleSweepResult> b = Task.Run(ReadTogether);
        ready.SignalAndWait();
        CapsuleSweepResult[] results = await Task.WhenAll(a, b);
        HitContains(results[0], 1.25, 1.25);
        SameBits(results[0], results[1]);

        CapsuleSweepResult ReadTogether()
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            ready.SignalAndWait();
            CapsuleSweepResult result = Sweep(world);
            lease.AssertCurrent();
            return result;
        }
    }

    [Fact]
    public void FirstSweepRefusesChangedInstalledTriangleAndNode()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
        var entry = Static(world, handle);
        ref var mesh = ref Sim(world).Shapes.GetShape<BepuPhysics.Collidables.Mesh>(entry.Shape.Index);
        var triangle = mesh.Triangles[0];
        mesh.Triangles[0].A.Y = 0.125f;
        Refused(Sweep(world));
        mesh.Triangles[0] = triangle;
        var node = mesh.Tree.Nodes[0];
        mesh.Tree.Nodes[0].A.Min.Y = 0.125f;
        Refused(Sweep(world));
        mesh.Tree.Nodes[0] = node;
        HitContains(Sweep(world), 1.25, 1.25);
    }

    static CapsuleSweepResult Sweep(BepuPhysicsWorld world) => world.SweepCapsuleCertified(
        new CapsuleQueryDimensions(0.25f, 1), Pose.At(new(-0.5f, 2, -0.25f)), new(0, -4, 0));

    static TriangleMeshShape SparseMesh(int count)
    {
        var vertices = new Vector3[count * 3];
        var indices = new int[vertices.Length];
        for (int i = 0; i < count; i++)
        {
            float x = i % 64 * 8, z = i / 64 * 8;
            vertices[i * 3] = new(x - 2, 0, z - 2);
            vertices[i * 3 + 1] = new(x + 2, 0, z - 2);
            vertices[i * 3 + 2] = new(x - 2, 0, z + 2);
            for (int j = 0; j < 3; j++) indices[i * 3 + j] = i * 3 + j;
        }
        return new(vertices, indices);
    }
}
