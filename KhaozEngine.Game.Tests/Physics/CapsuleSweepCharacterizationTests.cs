using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

// Diagnostic only, on a private one-box world. The raw-library leg intentionally characterizes an
// overload the legacy seam does not expose. It is not a production restricted-view bypass or fallback.
public class CapsuleSweepCharacterizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("grazing", 0.0001f, 0f)]
    [InlineData("grazing", 0.0001f, 0.17f)]
    [InlineData("grazing", 0.0001f, -0.11f)]
    [InlineData("grazing", 0.0005f, 0f)]
    [InlineData("grazing", 0.0005f, 0.17f)]
    [InlineData("grazing", 0.0005f, -0.11f)]
    [InlineData("grazing", 0.001f, 0f)]
    [InlineData("grazing", 0.001f, 0.17f)]
    [InlineData("grazing", 0.001f, -0.11f)]
    [InlineData("head-on", 0f, 0f)]
    [InlineData("clear", 0f, 0f)]
    [InlineData("endpoint", 0f, 0f)]
    public void PrivateBoxSweepReportsLegacyAndZeroProgressionOutcomes(string mode, float penetration, float targetX)
    {
        const float radius = 0.3f, cylinderLength = 0.9f, halfX = 0.0005f, halfZ = 0.0005f;
        float z = mode switch
        {
            "grazing" => radius + halfZ - penetration,
            "clear" => radius + halfZ + 0.01f,
            _ => 0f
        };
        Vector3 start = new(-1f, 0f, z);
        float length = 2f;
        if (mode == "endpoint")
        {
            targetX = halfX;
            start.X = -2f * radius;
            length = radius;
        }
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(halfX, 1f, halfZ)), Pose.At(new Vector3(targetX, 0f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Assert.Same(world, view.SourceWorld);
        lease.AssertCurrent();
        var capsule = new CapsuleShape(radius, cylinderLength);
        bool legacyHit = view.SweepCapsule(capsule, Pose.At(start), Vector3.UnitX, length, out SweepHit legacy, QueryFilter.StaticsOnly);

        // Read the actual private test world's simulation, under its physical read gate. There is one
        // static, no dynamics and no excluded body, so the raw handler uses exactly that selection.
        FieldInfo? simulationField = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(simulationField);
        BepuSim simulation = Assert.IsType<BepuSim>(simulationField.GetValue(world));
        var handler = new CaptureSweep();
        var pool = new BufferPool();
        try
        {
            simulation.Sweep(new Capsule(radius, cylinderLength), new RigidPose(start, Quaternion.Identity),
                new BodyVelocity(Vector3.UnitX), length, pool, ref handler,
                minimumProgression: 0f, convergenceThreshold: 0.000001f, maximumIterationCount: 64);
        }
        finally { pool.Clear(); }

        Vector3 probe = mode == "endpoint" ? start + Vector3.UnitX * length : new Vector3(targetX, 0f, z);
        var contacts = new CapsuleContact[16];
        CapsuleContactResult contactResult = ((IPhysicsCapsuleContacts)view).QueryCapsuleContacts(
            capsule, Pose.At(probe), 0.002f, contacts, QueryFilter.StaticsOnly);
        lease.AssertCurrent();
        Assert.True(contactResult.Complete);

        double outsideZ = Math.Max(0d, Math.Abs((double)z) - halfZ);
        double chordSquared = (double)radius * radius - outsideZ * outsideZ;
        bool analyticCrossing = chordSquared >= 0d;
        double chord = analyticCrossing ? Math.Sqrt(chordSquared) : 0d;
        double enter = targetX - (double)halfX - chord - start.X;
        double exit = targetX + (double)halfX + chord - start.X;
        output.WriteLine(FormattableString.Invariant(
            $"SWEEP mode={mode} penetration={penetration:R} targetX={targetX:R} z={z:R} length={length:R} analytic={analyticCrossing} enter={enter:R} exit={exit:R} legacyHit={legacyHit} legacyDistance={legacy.Distance:R} zeroProgressionHit={handler.Hit} zeroProgressionDistance={handler.Distance:R} contacts={contactResult.Written} contactError={contactResult.CertifiedErrorMetres:R}"));

        if (mode == "clear")
        {
            Assert.False(analyticCrossing);
            Assert.False(legacyHit);
            Assert.False(handler.Hit);
            Assert.Equal(0, contactResult.Written);
            return;
        }

        Assert.True(analyticCrossing);
        Assert.True(contactResult.Written > 0);
        if (mode == "endpoint")
        {
            Assert.Equal((double)length, enter);
            Assert.Contains(contacts.Take(contactResult.Written), contact =>
                contact.Normal.X < -0.99f && MathF.Abs(contact.Separation) <= contactResult.CertifiedErrorMetres);
            // The different raw tester predicates are the diagnostic subject. Neither boolean is
            // asserted to prove closed-endpoint clearance or a certified time bracket.
            return;
        }

        Assert.True(enter > 0d && exit < length);
        Assert.True(handler.Hit, "Zero-progression sweep missed the independently derived box crossing.");
        Assert.InRange((double)handler.Distance, enter - 0.001d, exit + 0.001d);
        if (legacyHit) Assert.InRange((double)legacy.Distance, enter - 0.001d, exit + 0.001d);
        if (mode == "grazing")
        {
            double depth = radius - outsideZ;
            Assert.Contains(contacts.Take(contactResult.Written), contact => contact.Normal.Z > 0.99f &&
                Math.Abs(contact.Separation + depth) <= contactResult.CertifiedErrorMetres);
        }
    }

    struct CaptureSweep : ISweepHitHandler
    {
        public bool Hit;
        public float Distance;
        public bool AllowTest(CollidableReference collidable) => collidable.Mobility == CollidableMobility.Static;
        public bool AllowTest(CollidableReference collidable, int child) => AllowTest(collidable);
        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
        {
            if (!Hit || t < Distance) { Hit = true; Distance = t; maximumT = t; }
        }
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            Hit = true;
            Distance = 0f;
            maximumT = 0f;
        }
    }
}
