using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Simultaneous readers on one Bepu world must reproduce the serial answers exactly.</summary>
public sealed class ConcurrentQueryParityTests
{
    const int Readers = 3;
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Fact]
    public void SimultaneousReadersReproduceSerialRaySweepPenetrationContactAndSupportResults()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle hull = Install(world);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([hull]);
        Func<IPhysicsWorld, IPhysicsQueryLease, string>[] queries = Queries();

        string[] serial;
        using (IPhysicsQueryLease lease = world.AcquireQueryReadLease())
            serial = queries.Select((query, index) => query(index % 2 == 0 ? world : view, lease)).ToArray();
        Assert.Contains(serial, s => s.Contains("Mesh contacts True", StringComparison.Ordinal));

        var results = new string[Readers][];
        using var barrier = new Barrier(Readers);
        var threads = new BoundedThread[Readers];
        for (int reader = 0; reader < Readers; reader++)
        {
            int slot = reader;
            threads[slot] = BoundedThread.Start(() =>
            {
                IPhysicsQueryLeaseSource source = slot == 0 ? world : (IPhysicsQueryLeaseSource)view;
                using IPhysicsQueryLease lease = source.AcquireQueryReadLease();
                var answers = new string[queries.Length];
                for (int i = 0; i < queries.Length; i++)
                {
                    // Every reader holds its lease and starts each query together with the others.
                    BoundedThread.Arrive(barrier);
                    answers[i] = queries[i](i % 2 == 0 ? world : view, lease);
                }
                results[slot] = answers;
            });
        }
        foreach (BoundedThread thread in threads) thread.Join();
        foreach (string[] answers in results) Assert.Equal(serial, answers);
    }

    static StaticHandle Install(BepuPhysicsWorld world)
    {
        // A two-triangle floor whose diagonal seam exercises Bepu's mesh boundary smoothing.
        Vector3[] floor = [new(-4f, 0f, -4f), new(-4f, 0f, 4f), new(4f, 0f, 4f), new(4f, 0f, -4f)];
        world.AddStatic(new TriangleMeshShape(floor, [0, 2, 1, 0, 3, 2]), Pose.Identity);
        BoxShape cube = new(new Vector3(0.5f));
        world.AddStatic(new CompoundShape([new CompoundChild(cube, Pose.Identity),
            new CompoundChild(cube, Pose.At(new Vector3(0f, 1f, 0f)))]), Pose.At(new Vector3(8f, 1f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(1f, 0.5f, 1f)), Pose.At(new Vector3(0f, -0.5f, 12f)));
        world.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(0f, 1f, 20f)));
        return world.AddStatic(new ConvexHullShape(
        [
            new(-8.5f, 0.5f, -0.5f), new(-7.5f, 0.5f, -0.5f), new(-8.5f, 1.5f, -0.5f), new(-7.5f, 1.5f, -0.5f),
            new(-8.5f, 0.5f, 0.5f), new(-7.5f, 0.5f, 0.5f), new(-8.5f, 1.5f, 0.5f), new(-7.5f, 1.5f, 0.5f),
        ]), Pose.Identity);
    }

    static Func<IPhysicsWorld, IPhysicsQueryLease, string>[] Queries() =>
    [
        (world, _) => Ray(world, new(0.5f, 3f, 0.5f), -Vector3.UnitY),
        (world, _) => Ray(world, new(5f, 1.2f, 0f), Vector3.UnitX),
        (world, _) => Ray(world, new(-5f, 1f, 0f), -Vector3.UnitX),
        (world, _) => Sweep(world, new(1f, 3f, 1f), -Vector3.UnitY),
        (world, _) => Sweep(world, new(5f, 1.4f, 0f), Vector3.UnitX),
        (world, _) => Sweep(world, new(-5f, 1f, 0f), -Vector3.UnitX),
        (world, _) => Penetration(world, new(7.6f, 1.2f, 0f)),
        (world, _) => Penetration(world, new(-7.6f, 1f, 0f)),
        (world, _) => Penetration(world, new(1f, 0.7f, 1f)),
        (world, _) => Contacts(world, "Mesh", new(0.02f, 0.751f, 0f)),
        (world, _) => Contacts(world, "Compound", new(7.25f, 1.2f, 0f)),
        (world, _) => Certified(world, new(0f, 1f, 17f), new(0f, 0f, 4f)),
        (world, lease) => Footprint(world, lease, 0f, 0f),
        (world, lease) => Footprint(world, lease, 0f, 12f),
        (world, lease) => Neighborhood(world, lease, new(0f, 0.751f, 12f)),
        (world, lease) => Footprint(world, lease, 0.15f, 12.1f),
    ];

    static string Ray(IPhysicsWorld world, Vector3 origin, Vector3 direction) =>
        $"Ray {world.Raycast(origin, direction, 8f, out RayHit hit)} {hit}";

    static string Sweep(IPhysicsWorld world, Vector3 origin, Vector3 direction) =>
        $"Sweep {world.SweepCapsule(Capsule, Pose.At(origin), direction, 6f, out SweepHit hit)} {hit}";

    static string Penetration(IPhysicsWorld world, Vector3 position) =>
        $"Penetration {world.ComputePenetration(Capsule, Pose.At(position), out Vector3 mtv)} {mtv}";

    static string Contacts(IPhysicsWorld world, string label, Vector3 position)
    {
        var destination = new CapsuleContact[64];
        CapsuleContactResult result = Assert.IsAssignableFrom<IPhysicsCapsuleContacts>(world)
            .QueryCapsuleContacts(Capsule, Pose.At(position), 0.05f, destination);
        return $"{label} contacts {result.Complete && result.Written > 0} {result} " +
            string.Join(";", destination.Take(result.Written));
    }

    static string Certified(IPhysicsWorld world, Vector3 position, Vector3 displacement) =>
        $"Certified {Assert.IsAssignableFrom<IPhysicsCapsuleSweep>(world).SweepCapsuleCertified(Capsule, Pose.At(position), displacement)}";

    static string Footprint(IPhysicsWorld world, IPhysicsQueryLease lease, float x, float z)
    {
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[1024];
        SupportFootprintResult result = Assert.IsAssignableFrom<IPhysicsSupportFootprint>(world)
            .QuerySupportFootprint(lease, SupportFootprintTests.Request(x, z), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        return $"Footprint {result.Status} {result.Members} {result.Witnesses} {result.AxisVerdict} {result.FootVerdict} " +
            string.Join(";", members.Take(result.Members)) + " " +
            string.Join(",", joins.Take(result.Members * result.JoinWordsPerRow));
    }

    static string Neighborhood(IPhysicsWorld world, IPhysicsQueryLease lease, Vector3 position)
    {
        var elements = new SupportElement[64];
        var joins = new ulong[64];
        SupportNeighborhoodResult result = Assert.IsAssignableFrom<IPhysicsSupportNeighborhood>(world)
            .QuerySupportNeighborhood(lease, Capsule, Pose.At(position), 0.01f, elements, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        return $"Neighborhood {result.Status} {result.Elements} " + string.Join(";", elements.Take(result.Elements)) +
            " " + string.Join(",", joins.Take(result.Elements * result.JoinWordsPerRow));
    }
}
