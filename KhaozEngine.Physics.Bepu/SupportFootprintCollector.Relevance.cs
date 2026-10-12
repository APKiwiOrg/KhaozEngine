using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportFootprintCollector
{
    // The first pass retains possible contributors. Below-band members are revisited only after
    // both complete contact bounds are known, and can affect only that fixed retained set or a guard.
    bool _belowBandPass, _hasBelowBand;
    int _primaryCount;

    internal bool BeginBelowBandPass()
    {
        _belowBandPass = true;
        _primaryCount = _count;
        return _hasBelowBand;
    }

    CapsuleFeatureStatus Retain(in Member member)
    {
        bool below = BelowBand(member);
        if (!_belowBandPass)
        {
            if (below) { _hasBelowBand = true; return CapsuleFeatureStatus.Complete; }
        }
        else
        {
            if (!below) return CapsuleFeatureStatus.Complete;
            bool needed = false;
            for (int proposal = 0; proposal < 2; proposal++)
                if ((member.Value.ProposalMask & (1 << proposal)) != 0 &&
                    (!_hasContactGeometry[proposal] || _contactHigh[proposal] >= _request.BandLower))
                    needed = true;
            if (!needed && member.Polygon is { } polygon)
                for (int i = 0; i < _primaryCount; i++)
                {
                    if (_members[i].Polygon is not { } other) continue;
                    GeometrySign joined = SupportNeighborhoodJoins.Joined(polygon, other, _request.ContactBand, _kernel);
                    if (joined == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                    if (joined == GeometrySign.Positive) { needed = true; break; }
                }
            if (!needed) return CapsuleFeatureStatus.Complete;
        }
        SupportQueryDiagnostics.Record(SupportQueryCounter.Members);
        if (++_count > SupportFootprintResult.MaximumElements) return CapsuleFeatureStatus.CapacityExceeded;
        _members[_count - 1] = member;
        return CapsuleFeatureStatus.Complete;
    }

    bool BelowBand(in Member member)
    {
        SupportFootprintMember value = member.Value;
        if (member.Polygon is { } polygon)
        {
            if (SupportFootprintPolygon.Orientation(polygon) != GeometrySign.Positive) return false;
            FeaturePoint point = polygon.Vertices[0];
            FeatureNumber plane = point.Y.Add(polygon.Normal.X.Multiply(
                point.X.Subtract(FeatureNumber.Exact(_request.Axis.X)), exactZeroProducts: true).Add(
                polygon.Normal.Z.Multiply(point.Z.Subtract(FeatureNumber.Exact(_request.Axis.Y)),
                    exactZeroProducts: true)).Divide(polygon.Normal.Y));
            if (!plane.IsResolved) return false;
            double upper = value.HasDiscMaximum ? Math.Min(plane.Bounds.Upper, value.DiscHeightUpper) : plane.Bounds.Upper;
            return upper < _request.BandLower;
        }
        if (value.WitnessCount == 0) return false;
        for (int i = 0; i < value.WitnessCount; i++)
        {
            SupportFootprintWitness witness = i == 0 ? value.First : value.Second;
            if (!(witness.UpwardLower > 0)) return false;
            GeometryInterval nx = Enclose(witness.Normal.X, witness.NormalError);
            GeometryInterval nz = Enclose(witness.Normal.Z, witness.NormalError);
            GeometryInterval ny = GeometryInterval.Enclose(witness.UpwardLower,
                Enclose(witness.Normal.Y, witness.NormalError).Upper);
            GeometryInterval y = Enclose(witness.Position.Y, witness.PositionErrorMetres);
            GeometryInterval plane = y.Add(nx.Multiply(Enclose(witness.Position.X, witness.PositionErrorMetres)
                .Subtract(GeometryInterval.Exact(_request.Axis.X))).Add(nz.Multiply(
                    Enclose(witness.Position.Z, witness.PositionErrorMetres).Subtract(GeometryInterval.Exact(_request.Axis.Y))))
                .Divide(ny));
            if (!plane.IsResolved || Math.Min(plane.Upper, y.Upper) >= _request.BandLower) return false;
        }
        return true;
    }

    static GeometryInterval Enclose(float value, float error) => error == 0 ? GeometryInterval.Exact(value) :
        GeometryInterval.Enclose(Math.BitDecrement((double)value - error), Math.BitIncrement((double)value + error));
}
