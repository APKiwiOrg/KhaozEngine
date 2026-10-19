using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Physics;

internal sealed class SupportFootprintProofReuseFixture : IDisposable
{
    readonly SupportQueryDiagnostics.ThreadState _prior =
        SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));

    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
    internal SupportFootprintReuseDomain Domain { get; } = new(new object(), new object());
    internal SupportFootprintRequest Request { get; } = SupportFootprintTests.Request();
    internal SupportFootprintMember[] Members { get; } = new SupportFootprintMember[256];
    internal ulong[] Joins { get; } = new ulong[1024];

    internal SupportFootprintProofReuseFixture(bool empty = false)
    {
        if (!empty) World.AddStatic(new BoxShape(new(1, 0.1f, 1)), Pose.At(new(0, -0.1f, 0)));
    }

    internal IPhysicsSupportFootprintReuseSession Open(IPhysicsQueryLease lease)
    {
        Xunit.Assert.True(((IPhysicsSupportFootprintReuseSource)World).TryOpenSupportFootprintReuse(lease, out var session));
        Xunit.Assert.NotNull(session);
        session.ResetSource(Domain);
        return session;
    }

    internal SupportFootprintResult Query(IPhysicsQueryLease lease) =>
        World.QuerySupportFootprint(lease, Request, Members, Joins);

    internal long[] Bits(in SupportFootprintResult result) => Bits(result, Members, Joins);

    // Enumerate named fields, including both inline witness slots, rather than managed struct padding.
    internal static long[] Bits(in SupportFootprintResult result, ReadOnlySpan<SupportFootprintMember> members,
        ReadOnlySpan<ulong> joins)
    {
        var bits = new List<long> { (int)result.Status, result.Members, result.Witnesses,
            result.RequiredElements, result.JoinWordsPerRow };
        Verdict(result.AxisVerdict);
        Verdict(result.FootVerdict);
        foreach (SupportFootprintMember member in members[..result.Members])
        {
            bits.Add(member.Static.Value); bits.Add((int)member.Kind); bits.Add(member.ElementId);
            Vector(member.PlanePoint); Float(member.PlanePointErrorMetres);
            Vector(member.Normal); Float(member.NormalError); Double(member.UpwardLower);
            bits.Add(member.HasDiscMaximum ? 1 : 0); Double(member.DiscHeightLower); Double(member.DiscHeightUpper);
            bits.Add(member.ProposalMask); bits.Add(member.WitnessCount);
            Witness(member.First); Witness(member.Second);
        }
        foreach (ulong word in joins[..(result.Members * result.JoinWordsPerRow)]) bits.Add(unchecked((long)word));
        return [.. bits];

        void Float(float value) => bits.Add(BitConverter.SingleToInt32Bits(value));
        void Double(double value) => bits.Add(BitConverter.DoubleToInt64Bits(value));
        void Vector(Vector3 value) { Float(value.X); Float(value.Y); Float(value.Z); }
        void Verdict(SupportProposalVerdict value)
        { bits.Add((int)value.Status); Double(value.LowestLower); Double(value.LowestUpper); }
        void Witness(SupportFootprintWitness value)
        {
            bits.Add((int)value.Role); Vector(value.Position); Float(value.PositionErrorMetres);
            Vector(value.Normal); Float(value.NormalError); Double(value.UpwardLower);
        }
    }

    public void Dispose()
    {
        World.Dispose();
        SupportQueryDiagnostics.Exchange(_prior);
    }
}
