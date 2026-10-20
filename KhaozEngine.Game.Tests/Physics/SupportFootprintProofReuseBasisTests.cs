using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportFootprintProofReuseBasisTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("X", 1)]
    [InlineData("Y", 1)]
    [InlineData("Z", 1)]
    [InlineData("X", int.MinValue)]
    [InlineData("Y", int.MinValue)]
    [InlineData("Z", int.MinValue)]
    public void SessionOriginComparisonsRejectOneBitWithEveryOtherBasisComponentFixed(string axis, int bits)
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        using var view = fixture.World.CreateQueryViewExcludingStatics([]);
        using var lease = fixture.World.AcquireQueryReadLease();
        using var session = fixture.Open(lease, view);
        var capability = (IPhysicsSupportFootprint)view;
        capability.QuerySupportFootprint(lease, fixture.Request, fixture.Members, fixture.Joins);
        var type = session.GetType();
        var matches = type.GetMethod("Matches", Instance)!.CreateDelegate<Func<IPhysicsQueryLease, bool>>(session);
        var current = type.GetMethod("CurrentBasis", Instance)!;
        var before = SupportQueryDiagnostics.Snapshot();
        Assert.True(matches(lease));
        Assert.True((bool)current.Invoke(session, null)!);

        // Synthetic session comparison only. Public entry points reject a wrapper even with identical metadata.
        var same = new OriginLease(lease, lease.Origin);
        var changed = new OriginLease(lease, Origin(axis, bits));
        Assert.True(matches(same));
        Assert.Same(same.SourceWorld, changed.SourceWorld);
        Assert.Equal(same.GeometryGeneration, changed.GeometryGeneration);
        Assert.False(matches(changed));
        var source = (IPhysicsSupportFootprintReuseSource)view;
        Assert.Throws<ArgumentException>(() => source.TryOpenSupportFootprintReuse(same, out _));
        Assert.Throws<ArgumentException>(() => source.TryOpenSupportFootprintReuse(changed, out _));
        Assert.Throws<ArgumentException>(() => capability.QuerySupportFootprint(changed,
            fixture.Request, fixture.Members, fixture.Joins));

        // Clone without registering or disposing it. It borrows the original memo and domain, never their ownership.
        // Only one captured origin field changes. The live owner, real lease, generations and request key stay fixed.
        object captured = typeof(object).GetMethod("MemberwiseClone", Instance)!.Invoke(session, null)!;
        string fieldName = "_origin" + axis;
        type.GetField(fieldName, Instance)!.SetValue(captured, bits);
        foreach (var field in type.GetFields(Instance).Where(field => field.Name != fieldName))
            Assert.Equal(field.GetValue(session), field.GetValue(captured));
        Assert.False((bool)current.Invoke(captured, null)!);
        Assert.True((bool)current.Invoke(session, null)!);
        Assert.True(matches(lease));
        Assert.Equal(before, SupportQueryDiagnostics.Snapshot());
        Assert.Equal(1, SupportQueryDiagnostics.Retention.Keys);
    }

    [Theory]
    [InlineData("X", 0)]
    [InlineData("X", 1)]
    [InlineData("Y", 1)]
    [InlineData("Z", 1)]
    [InlineData("X", int.MinValue)]
    [InlineData("Y", int.MinValue)]
    [InlineData("Z", int.MinValue)]
    public void RebaseCannotExposeAnotherOriginAtTheSameLeaseGeneration(string axis, int bits)
    {
        using var fixture = new SupportFootprintProofReuseFixture();
        Vector3 origin = Origin(axis, bits);
        using var lease = fixture.World.AcquireQueryReadLease();
        long generation = lease.GeometryGeneration;
        Assert.Throws<InvalidOperationException>(() => fixture.World.Rebase(origin));
        lease.AssertCurrent();
        Assert.Equal(generation, lease.GeometryGeneration);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(fixture.World.Origin.X));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(fixture.World.Origin.Y));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(fixture.World.Origin.Z));
        lease.Dispose();
        fixture.World.Rebase(origin);
        using var next = fixture.World.AcquireQueryReadLease();
        Assert.Equal(generation + 1, next.GeometryGeneration);
        Assert.Equal(BitConverter.SingleToInt32Bits(origin.X), BitConverter.SingleToInt32Bits(next.Origin.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(origin.Y), BitConverter.SingleToInt32Bits(next.Origin.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(origin.Z), BitConverter.SingleToInt32Bits(next.Origin.Z));
        next.AssertCurrent();
    }

    static Vector3 Origin(string axis, int bits)
    {
        float value = BitConverter.Int32BitsToSingle(bits);
        return axis switch
        {
            "X" => new(value, 0, 0),
            "Y" => new(0, value, 0),
            "Z" => new(0, 0, value),
            _ => throw new ArgumentException("Unknown origin axis.", nameof(axis)),
        };
    }

    sealed class OriginLease(IPhysicsQueryLease original, Vector3 origin) : IPhysicsQueryLease
    {
        public IPhysicsWorld SourceWorld => original.SourceWorld;
        public Vector3 Origin => origin;
        public long GeometryGeneration => original.GeometryGeneration;
        public void AssertCurrent() => original.AssertCurrent();
        public void Dispose() { }
    }
}
