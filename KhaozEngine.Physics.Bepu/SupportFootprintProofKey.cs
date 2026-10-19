using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Every evaluator operand as integer bits. The session authenticates receiver, view and domain separately.</summary>
internal readonly record struct SupportFootprintProofKey(int AxisX, int AxisZ, int Radius, long BandLower,
    long BandUpper, int CosMaxSlope, int ContactBand, SupportFootprintProofKey.Proposal AxisProposal,
    SupportFootprintProofKey.Proposal FootProposal, QueryMobility Mobility, uint Layers, bool CullBackFaces,
    bool CertificationJoins)
{
    internal static SupportFootprintProofKey Create(in SupportFootprintRequest request, QueryFilter filter,
        bool certificationJoins) => new(Bits(request.Axis.X), Bits(request.Axis.Y), Bits(request.Radius),
        BitConverter.DoubleToInt64Bits(request.BandLower), BitConverter.DoubleToInt64Bits(request.BandUpper),
        Bits(request.CosMaxSlope), Bits(request.ContactBand), Proposal.Create(request.AxisProposal),
        Proposal.Create(request.FootProposal), filter.Mobility, filter.Layers, filter.CullBackFaces, certificationJoins);

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    internal readonly record struct Proposal(int Radius, int Length, int X, int Y, int Z,
        int Qx, int Qy, int Qz, int Qw, int Distance)
    {
        internal static Proposal Create(in SupportFootprintProposal proposal) => new(Bits(proposal.Probe.Radius),
            Bits(proposal.Probe.Length), Bits(proposal.Start.Position.X), Bits(proposal.Start.Position.Y),
            Bits(proposal.Start.Position.Z), Bits(proposal.Start.Orientation.X), Bits(proposal.Start.Orientation.Y),
            Bits(proposal.Start.Orientation.Z), Bits(proposal.Start.Orientation.W), Bits(proposal.Distance));
    }
}
