using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

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

    internal IPhysicsSupportFootprintReuseSession Open(IPhysicsQueryLease lease, IPhysicsWorld? receiver = null)
    {
        Xunit.Assert.True(((IPhysicsSupportFootprintReuseSource)(receiver ?? World)).TryOpenSupportFootprintReuse(lease, out var session));
        Xunit.Assert.NotNull(session);
        session.ResetSource(Domain);
        return session;
    }

    internal SupportFootprintResult Query(IPhysicsQueryLease lease) =>
        World.QuerySupportFootprint(lease, Request, Members, Joins);

    internal long[] Bits(in SupportFootprintResult result) => Bits(result, Members, Joins);

    internal static byte[] SerializePayloadBits(in SupportFootprintResult result,
        ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins)
    {
        long[] bits = Bits(result, members, joins);
        byte[] payload = new byte[bits.Length * sizeof(long)];
        for (int i = 0; i < bits.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(i * sizeof(long)), bits[i]);
        return payload;
    }

    public static TheoryData<string> NearMisses()
    {
        var rows = new TheoryData<string> { "axis.x", "axis.z", "radius", "band.lower", "band.upper",
            "slope", "contact-band.above", "contact-band.below", "axis.signed-zero", "filter.mobility",
            "filter.dynamics", "filter.invalid-mobility", "filter.layers", "filter.cull", "joins" };
        foreach (string proposal in new[] { "axis-proposal", "foot-proposal" })
            foreach (string field in new[] { "radius", "length", "x", "y", "z", "qx", "qy", "qz", "qw", "distance" })
                rows.Add(proposal + "." + field);
        return rows;
    }

    internal static (SupportFootprintRequest Request, QueryFilter Filter, bool Certification) NearMiss(string component,
        SupportFootprintRequest request)
    {
        switch (component)
        {
            case "axis.x": return (request with { Axis = request.Axis with { X = MathF.BitIncrement(request.Axis.X) } }, default, false);
            case "axis.z": return (request with { Axis = request.Axis with { Y = MathF.BitIncrement(request.Axis.Y) } }, default, false);
            case "axis.signed-zero": return (request with { Axis = new(-0.0f, request.Axis.Y) }, default, false);
            case "radius": return (request with { Radius = MathF.BitIncrement(request.Radius) }, default, false);
            case "band.lower": return (request with { BandLower = Math.BitIncrement(request.BandLower) }, default, false);
            case "band.upper": return (request with { BandUpper = Math.BitIncrement(request.BandUpper) }, default, false);
            case "slope": return (request with { CosMaxSlope = MathF.BitIncrement(request.CosMaxSlope) }, default, false);
            case "contact-band.above": return (request with { ContactBand = MathF.BitIncrement(request.ContactBand) }, default, false);
            case "contact-band.below": return (request with { ContactBand = MathF.BitDecrement(request.ContactBand) }, default, false);
            case "filter.mobility": return (request, QueryFilter.StaticsOnly, false);
            case "filter.dynamics": return (request, QueryFilter.DynamicsOnly, false);
            case "filter.invalid-mobility": return (request, new((QueryMobility)(-1)), false);
            case "filter.layers": return (request, new(Layers: 1), false);
            case "filter.cull": return (request, new() { CullBackFaces = true }, false);
            case "joins": return (request, default, true);
        }
        string[] parts = component.Split('.');
        bool axis = parts[0] == "axis-proposal";
        SupportFootprintProposal proposal = axis ? request.AxisProposal : request.FootProposal;
        Vector3 position = proposal.Start.Position;
        Quaternion rotation = proposal.Start.Orientation;
        switch (parts[1])
        {
            case "radius": proposal = proposal with { Probe = new(MathF.BitIncrement(proposal.Probe.Radius), proposal.Probe.Length) }; break;
            case "length": proposal = proposal with { Probe = new(proposal.Probe.Radius, MathF.BitIncrement(proposal.Probe.Length)) }; break;
            case "x": position.X = MathF.BitIncrement(position.X); break;
            case "y": position.Y = MathF.BitIncrement(position.Y); break;
            case "z": position.Z = MathF.BitIncrement(position.Z); break;
            case "qx": rotation.X = MathF.BitIncrement(rotation.X); break;
            case "qy": rotation.Y = MathF.BitIncrement(rotation.Y); break;
            case "qz": rotation.Z = MathF.BitIncrement(rotation.Z); break;
            case "qw": rotation.W = MathF.BitIncrement(rotation.W); break;
            case "distance": proposal = proposal with { Distance = MathF.BitIncrement(proposal.Distance) }; break;
            default: throw new ArgumentException("Unknown key component.", nameof(component));
        }
        proposal = proposal with { Start = new(position, rotation) };
        return (axis ? request with { AxisProposal = proposal } : request with { FootProposal = proposal }, default, false);
    }

    internal void FillSentinels()
    {
        for (int i = 0; i < Members.Length; i++) Members[i] = new(default, default, int.MinValue + i,
            new(i, -0.0f, -i), 0, default, 0, 0, false, 0, 0, byte.MaxValue, 0, default, default);
        for (int i = 0; i < Joins.Length; i++) Joins[i] = 73UL + (ulong)i;
    }

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
