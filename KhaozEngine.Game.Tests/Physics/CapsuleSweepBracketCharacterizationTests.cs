using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities.Memory;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

// Diagnostic access to the real convex task's raw bracket. Pointers belong to the one-static private
// test world and remain inside its physical read lease. No production query path uses this access.
public class CapsuleSweepBracketCharacterizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("head-on", 1)]
    [InlineData("head-on", 64)]
    [InlineData("grazing", 1)]
    [InlineData("grazing", 64)]
    [InlineData("clear", 1)]
    [InlineData("clear", 64)]
    [InlineData("endpoint", 1)]
    [InlineData("endpoint", 64)]
    public unsafe void RawConvexTaskReportsItsRemainingTimeBracket(string mode, int iterationBudget)
    {
        const float radius = 0.3f, cylinderLength = 0.9f, halfX = 0.0005f, halfZ = 0.0005f;
        float z = mode switch
        {
            "grazing" => radius + halfZ - 0.0001f,
            "clear" => radius + halfZ + 0.01f,
            _ => 0f
        };
        Vector3 start = new(-1f, 0f, z);
        float targetX = 0f, length = 2f;
        if (mode == "endpoint") { targetX = halfX; start.X = -2f * radius; length = radius; }
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(halfX, 1f, halfZ)), Pose.At(new Vector3(targetX, 0f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Assert.Same(world, view.SourceWorld);
        lease.AssertCurrent();
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(world));
        Assert.Equal(1, simulation.Statics.Count);
        ref var target = ref simulation.Statics[0];
        Assert.Equal(default(Box).TypeId, target.Shape.Type);
        simulation.Shapes[target.Shape.Type].GetShapeData(target.Shape.Index, out void* targetData, out _);
        var capsule = new Capsule(radius, cylinderLength);
        SweepTask? task = simulation.NarrowPhase.SweepTaskRegistry.GetTask(capsule.TypeId, target.Shape.Type);
        Assert.NotNull(task);
        var filter = new AllowOnlyPrivatePair();
        var pool = new BufferPool();
        bool hit;
        float t0, t1;
        try
        {
            hit = task.Sweep(&capsule, capsule.TypeId, Quaternion.Identity, new BodyVelocity(Vector3.UnitX),
                targetData, target.Shape.Type, target.Pose.Position - start, target.Pose.Orientation, new BodyVelocity(Vector3.Zero),
                length, 0f, 0.000001f, iterationBudget, ref filter, simulation.Shapes,
                simulation.NarrowPhase.SweepTaskRegistry, pool, out t0, out t1, out _, out _);
        }
        finally { pool.Clear(); }

        Vector3 witness = mode == "endpoint" ? start + Vector3.UnitX * length : new Vector3(targetX, 0f, z);
        var contacts = new CapsuleContact[16];
        CapsuleContactResult contactResult = ((IPhysicsCapsuleContacts)view).QueryCapsuleContacts(
            new CapsuleShape(radius, cylinderLength), Pose.At(witness), 0.002f, contacts, QueryFilter.StaticsOnly);
        lease.AssertCurrent();
        Assert.True(contactResult.Complete);
        Assert.True(float.IsFinite(t0) && float.IsFinite(t1));
        double outsideZ = Math.Max(0d, Math.Abs((double)z) - halfZ);
        double chordSquared = (double)radius * radius - outsideZ * outsideZ;
        bool analyticCrossing = chordSquared >= 0d;
        double chord = analyticCrossing ? Math.Sqrt(chordSquared) : 0d;
        double enter = targetX - (double)halfX - chord - start.X;
        double exit = targetX + (double)halfX + chord - start.X;
        output.WriteLine(FormattableString.Invariant(
            $"BRACKET mode={mode} iterationBudget={iterationBudget} hit={hit} lower={t0:R} upper={t1:R} width={(double)t1 - t0:R} noninverted={t0 <= t1} length={length:R} analytic={analyticCrossing} enter={enter:R} exit={exit:R} contacts={contactResult.Written} contactError={contactResult.CertifiedErrorMetres:R}"));

        if (mode == "clear")
        {
            Assert.False(analyticCrossing);
            Assert.False(hit);
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
            return;
        }
        Assert.True(enter > 0d && exit < length);
        if (iterationBudget == 64)
        {
            Assert.True(hit, "The 64-iteration raw task missed the independently derived interior crossing.");
            Assert.InRange((double)t1, enter - 0.001d, exit + 0.001d);
        }
        // One-iteration booleans/brackets are observations. Do not relabel an unfinished search as
        // clear or claim which termination branch fired without an actual branch trace.
    }

    struct AllowOnlyPrivatePair : ISweepFilter
    {
        public bool AllowTest(int childA, int childB) => true;
    }
}
