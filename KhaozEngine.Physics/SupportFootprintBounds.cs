using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Outward binary32 bounds of the complete admission column and both retained upright proposal paths.
/// This shared owner also supplies scope guards. These search bounds never extend contribution reach.</summary>
public readonly record struct SupportFootprintBounds(Vector3 Minimum, Vector3 Maximum)
{
    /// <summary>Refuses a zero slope cosine, invalid request, overflow or unbounded binary32 conversion.</summary>
    public static bool TryCreate(in SupportFootprintRequest request, out SupportFootprintBounds bounds)
    {
        bounds = default;
        if (!TryColumn(request, out double bottom, out double top)) return false;
        double radius = Up((double)request.Radius + request.ContactBand);
        double x0 = Down(request.Axis.X - radius), x1 = Up(request.Axis.X + radius);
        double z0 = Down(request.Axis.Y - radius), z1 = Up(request.Axis.Y + radius);
        EncloseHorizontal(request.AxisProposal, request.ContactBand, ref x0, ref x1, ref z0, ref z1);
        EncloseHorizontal(request.FootProposal, request.ContactBand, ref x0, ref x1, ref z0, ref z1);
        Vector3 minimum = new(Lower(x0), Lower(bottom), Lower(z0));
        Vector3 maximum = new(Upper(x1), Upper(top), Upper(z1));
        if (!Finite(minimum) || !Finite(maximum)) return false;
        bounds = new(minimum, maximum);
        return true;
    }

    // Column endpoints stay binary64 for narrow admission. Rounding the broadphase box to binary32 must not
    // admit a polygon which is proved outside the column.
    internal static bool TryColumn(in SupportFootprintRequest request, out double bottom, out double top)
    {
        bottom = top = 0;
        double c = request.CosMaxSlope, r = request.Radius, e = request.ContactBand;
        if (!float.IsFinite(request.Axis.X) || !float.IsFinite(request.Axis.Y) ||
            !float.IsFinite(request.Radius) || r < 0.01f || !double.IsFinite(request.BandLower) ||
            !double.IsFinite(request.BandUpper) || request.BandLower > request.BandUpper ||
            !double.IsFinite(c) || c <= 0 || c > 1 || request.ContactBand != 0.0001f ||
            !Valid(request.AxisProposal) || !Valid(request.FootProposal)) return false;
        // A binary32 square is exact in binary64. The subtraction, root and quotient are enclosed outward.
        double square = c == 1 ? 0 : Up(1 - c * c);
        double root = square == 0 ? 0 : Up(Math.Sqrt(square));
        if (Math.FusedMultiplyAdd(root, root, -square) < 0) return false;
        double tangent = c == 1 ? 0 : Up(root / c);
        bottom = Down(Down(request.BandLower - Math.Max(Up(r * tangent), 0.001)) - e);
        top = Up(Up(Up(Up(request.BandUpper + 0.001) + Up(2 * r)) + 0.01) + e);
        EncloseVertical(request.AxisProposal, e, ref bottom, ref top);
        EncloseVertical(request.FootProposal, e, ref bottom, ref top);
        return double.IsFinite(bottom) && double.IsFinite(top);
    }

    static bool Valid(in SupportFootprintProposal proposal) => proposal.Probe is not null &&
        Finite(proposal.Start.Position) &&
        proposal.Start.Orientation == Quaternion.Identity && float.IsFinite(proposal.Probe.Radius) &&
        proposal.Probe.Radius > 0 && float.IsFinite(proposal.Probe.Length) && proposal.Probe.Length >= 0 &&
        float.IsFinite(proposal.Distance) && proposal.Distance >= 0;

    static void EncloseVertical(in SupportFootprintProposal proposal, double band, ref double bottom, ref double top)
    {
        double half = Up((double)proposal.Probe.Radius + (double)proposal.Probe.Length / 2);
        double start = proposal.Start.Position.Y;
        bottom = Math.Min(bottom, Down(Down(Down(start - proposal.Distance) - half) - band));
        top = Math.Max(top, Up(Up(start + half) + band));
        // Include the installed float centre/endpoint and capsule arithmetic as well as the real expressions.
        float representedHalf = proposal.Probe.Radius + proposal.Probe.Length / 2;
        float representedEnd = proposal.Start.Position.Y - proposal.Distance;
        bottom = Math.Min(bottom, Down((double)(representedEnd - representedHalf - (float)band)));
        top = Math.Max(top, Up((double)(proposal.Start.Position.Y + representedHalf + (float)band)));
    }

    static void EncloseHorizontal(in SupportFootprintProposal proposal, float band,
        ref double x0, ref double x1, ref double z0, ref double z1)
    {
        double reach = Up((double)proposal.Probe.Radius + band);
        x0 = Math.Min(x0, Down(proposal.Start.Position.X - reach));
        x1 = Math.Max(x1, Up(proposal.Start.Position.X + reach));
        z0 = Math.Min(z0, Down(proposal.Start.Position.Z - reach));
        z1 = Math.Max(z1, Up(proposal.Start.Position.Z + reach));
    }

    static double Up(double value) => Math.BitIncrement(value);
    static double Down(double value) => Math.BitDecrement(value);
    static float Lower(double value)
    {
        float result = (float)value;
        return result > value ? MathF.BitDecrement(result) : result;
    }
    static float Upper(double value)
    {
        float result = (float)value;
        return result < value ? MathF.BitIncrement(result) : result;
    }
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
