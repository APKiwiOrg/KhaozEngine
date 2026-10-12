using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("step")]
    [InlineData("dynamic add")]
    [InlineData("dynamic remove")]
    [InlineData("dynamic velocity")]
    [InlineData("unchanged rebase")]
    [InlineData("missing static removal")]
    public void InstalledTopologySurvivesChangesOutsideStaticGeometry(string mutation)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        var dynamic = world.AddDynamic(new BoxShape(Vector3.One), Pose.At(new(20, 0, 0)), new(1));
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult cold;
        using (var lease = world.AcquireQueryReadLease())
            cold = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
        var saved = members.Take(cold.Members).ToArray();
        switch (mutation)
        {
            case "step": world.Step(1f / 30); break;
            case "dynamic add": world.AddDynamic(new BoxShape(Vector3.One), Pose.At(new(30, 0, 0)), new(1)); break;
            case "dynamic remove": world.RemoveDynamic(dynamic); break;
            case "dynamic velocity": world.SetDynamicVelocity(dynamic, Vector3.UnitX, Vector3.Zero); break;
            case "unchanged rebase": world.Rebase(Vector3.Zero); break;
            case "missing static removal": world.RemoveStatic(new(-1)); break;
        }
        int validations = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportLocalValidation && entering) validations++;
        });
        try
        {
            using var lease = world.AcquireQueryReadLease();
            Assert.True(lease.GeometryGeneration > cold.GeometryGeneration);
            Assert.Throws<InvalidOperationException>(() => Capability(world).AssertFootprintCurrent(cold, lease));
            var next = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, next.Status);
            Assert.Equal(saved, members.Take(next.Members));
            Assert.Equal(0, validations);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }

    [Theory]
    [InlineData("add", 0)]
    [InlineData("remove", 0)]
    [InlineData("shape replacement", 0.125)]
    [InlineData("pose replacement", 0.125)]
    [InlineData("description replacement", 0.25)]
    [InlineData("rebase", -0.125)]
    public void EverySupportedStaticMutationRecapturesAndPublishesLiveGeometry(string mutation, double height)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var floor = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        var remote = world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(20, 0, 0)));
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult cold;
        using (var lease = world.AcquireQueryReadLease())
            cold = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
        switch (mutation)
        {
            case "add": world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(30, 0, 0))); break;
            case "remove": world.RemoveStatic(remote); break;
            case "rebase": world.Rebase(new(0, 0.125f, 0)); break;
            default:
                // The static seam replaces shapes, poses and descriptions through removal and installation.
                world.RemoveStatic(floor);
                floor = world.AddStatic(new BoxShape(new(2, mutation == "pose replacement" ? 1 : 1.125f, 2)),
                    Pose.At(new(0, mutation == "shape replacement" ? -1 : -0.875f, 0)));
                break;
        }
        int validations = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportLocalValidation && entering) validations++;
        });
        try
        {
            using var lease = world.AcquireQueryReadLease();
            Assert.Throws<InvalidOperationException>(() => Capability(world).AssertFootprintCurrent(cold, lease));
            var next = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, next.Status);
            Assert.True(validations > 0);
            var top = members.Take(next.Members).Single(m => m.HasDiscMaximum);
            Assert.Equal(floor, top.Static);
            Assert.Equal(height, top.DiscHeightLower);
            Assert.Equal(height, top.DiscHeightUpper);
            validations = 0;
            Assert.Equal(next, Capability(world).QuerySupportFootprint(lease, Request(), members, joins));
            Assert.Equal(0, validations);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }
}
