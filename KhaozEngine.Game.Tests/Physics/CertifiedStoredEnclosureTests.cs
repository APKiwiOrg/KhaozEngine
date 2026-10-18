using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedStoredEnclosureTests
{
    public static IEnumerable<object[]> Routes() => CertifiedOperationalPruningTests.RootRoutes();

    [Theory]
    [MemberData(nameof(Routes))]
    public void InDomainStoredEnclosureKeepsCorrectHitAndClear(string family, bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(Shape(family), new(Vector3.Zero,
            family == "box" ? new(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity));
        StaticHandle[]? exclusions = view
            ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 100, 0)))] : null;
        // The original native bounds are about one metre. Padding must actually reside
        // in the installed tree, rather than being applied to the identity aperture.
        var simulation = Sim(world);
        int index = simulation.Statics[simulation.Statics.HandleToIndex[Static(world, handle).Handle.Value]].BroadPhaseIndex;
        var link = simulation.BroadPhase.StaticTree.Leaves[index];
        var node = simulation.BroadPhase.StaticTree.Nodes[link.NodeIndex];
        var stored = link.ChildIndex == 0 ? node.A : node.B;
        Assert.True(stored.Min.X < -16 && stored.Max.X > 16);
        HitContains(Read(world, new(3, 0, 0), new(-4, 0, 0), length: 0,
            useDimensions: dimensions, exclusions: exclusions), family == "mesh" ? 2.75 : 1.75, family == "mesh" ? 2.75 : 1.75);
        Clear(Read(world, new(3, 0, 0), new(0, 0, 0.125f), length: 0,
            useDimensions: dimensions, exclusions: exclusions), 0.125f);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void InDomainCancellationReachesOperationalLeafRefusal(string family, bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        float rootX = family == "mesh" ? -0.25001f : -1.25001f;
        world.AddStatic(Shape(family), new(new(rootX, 0, 0),
            family == "box" ? new(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity));
        Vector3 centre = new(1000000, 0, 0);
        // Strict forward separation rounds to operational tangency at the endpoint.
        Assert.Equal(family == "mesh" ? -0.25f : -1.25f, (rootX - centre.X) + centre.X);
        Assert.True(rootX + (family == "mesh" ? 0d : 1d) < -0.25);
        Refused(Read(world, centre, -centre, length: 0, useDimensions: dimensions,
            exclusions: view ? [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 100, 0)))] : null));
    }

    [Theory]
    [MemberData(nameof(CertifiedStoredBoundsResidualTests.Routes), MemberType = typeof(CertifiedStoredBoundsResidualTests))]
    public void StoredPadDoesNotForceSeparatedPlankIntoLeafRefusal(bool view, bool dimensions)
    {
        foreach (bool compound in new[] { false, true })
        {
            var (scene, _, direction) = ContactFollowupScenes.Plate(MathF.PI / 60, compound);
            using (scene)
            {
                Vector2 start = ContactFollowupScenes.PlateCentre - 0.75f * direction;
                StaticHandle[]? exclusions = view ? [scene.World.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 100, 0)))] : null;
                foreach (var pair in new CapsuleQueryDimensions[] { new(0.4f, 0.59999996f), new(0.40000007f, 0.6000001f) })
                    Clear(Read(scene.World, new(start.X, 1.1010001f, start.Y), new(0, 0.4f, 0),
                        radius: pair.Radius, length: pair.Length, useDimensions: dimensions, exclusions: exclusions), 0.4f);
            }
        }
    }

    static PhysicsShape Shape(string family) => family switch
    {
        "sphere" => new SphereShape(1),
        "capsule" => new CapsuleShape(1, 0.000001f),
        "cylinder" => new CylinderShape(1, 0.000001f),
        "box" => new BoxShape(new(1)),
        "hull" => new ConvexHullShape([new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
            new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1)]),
        "mesh" => new TriangleMeshShape([new(0, -2, -2), new(0, 2, -2), new(0, 0, 2)], [0, 1, 2]),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
