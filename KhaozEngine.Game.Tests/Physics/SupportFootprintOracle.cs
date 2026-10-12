using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Physics;

internal readonly record struct FootprintOracleWitness(SupportWitnessRole Role, OracleVector Position,
    OracleVector Normal, double UpwardLower);

internal sealed record FootprintOracleMember(StaticHandle Static, int ElementId, bool Admitted,
    bool HasDiscMaximum, double Plane, double Maximum, OracleVector Normal,
    IReadOnlyList<FootprintOracleWitness> Witnesses, bool WitnessInsideClosedDisc)
{
    internal OracleFace? Face { get; init; }
    internal SupportFootprintExactOracle.Bounds? PlaneBounds { get; init; }
    internal SupportFootprintExactOracle.Bounds? MaximumBounds { get; init; }
    internal double UpwardLower => Normal.Y;
}

internal readonly record struct FootprintOracleSupport(SupportStatus Status, double Height, double HeightLower,
    double HeightUpper, OracleVector Normal, OracleVector Witness, StaticHandle? Static, int ElementId);

/// <summary>Independent rational polygon geometry and binary64 curved geometry over installed floats. No production clipping, joining, extremum or
/// support selection is used. Curved extrema enumerate spherical ends, finite side patches and planar caps.</summary>
internal static class SupportFootprintOracle
{
    internal static (double Bottom, double Top) Volume(in SupportFootprintRequest request)
    {
        if (!(request.CosMaxSlope > 0)) throw new NotSupportedException("Zero cosine has no finite column.");
        double c = request.CosMaxSlope, r = request.Radius, e = request.ContactBand;
        double t = Math.BitIncrement(Math.Sqrt(Math.BitIncrement(1 - Math.BitDecrement(c * c))) / c);
        double bottom = Math.BitDecrement(request.BandLower - Math.Max(Math.BitIncrement(r * t), 0.001) - e);
        double top = Math.BitIncrement(request.BandUpper + 0.001 + 2 * r + 0.01 + e);
        foreach (SupportFootprintProposal proposal in new[] { request.AxisProposal, request.FootProposal })
        {
            double half = (double)proposal.Probe.Radius + (double)proposal.Probe.Length / 2;
            bottom = Math.Min(bottom, Math.BitDecrement(proposal.Start.Position.Y - proposal.Distance - half - e));
            top = Math.Max(top, Math.BitIncrement(proposal.Start.Position.Y + half + e));
            // SG-3 encloses the represented proposal arithmetic as well as the real capsule path.
            float installedHalf = proposal.Probe.Radius + proposal.Probe.Length / 2;
            float installedEnd = proposal.Start.Position.Y - proposal.Distance;
            bottom = Math.Min(bottom, Math.BitDecrement((double)(installedEnd - installedHalf - request.ContactBand)));
            top = Math.Max(top, Math.BitIncrement((double)(proposal.Start.Position.Y + installedHalf + request.ContactBand)));
        }
        return (bottom, top);
    }

    internal static FootprintOracleMember? Polygon(OracleFace face, in SupportFootprintRequest request)
    {
        (double bottom, double top) = Volume(request);
        var expanded = SupportFootprintExactOracle.Disc(face, request.Axis.X, request.Axis.Y,
            InstalledPoseOracle.R.From(request.Radius) + InstalledPoseOracle.R.From(request.ContactBand));
        if (expanded is not { } column || column.Minimum.Lower > InstalledPoseOracle.R.From(top) ||
            column.Maximum.Upper < InstalledPoseOracle.R.From(bottom)) return null;
        var disc = SupportFootprintExactOracle.Disc(face, request.Axis.X, request.Axis.Y,
            InstalledPoseOracle.R.From(request.Radius));
        var plane = face.Normal.Y > 0 ? SupportFootprintExactOracle.Plane(face, request.Axis.X, request.Axis.Y)
            : (SupportFootprintExactOracle.Bounds?)null;
        OracleVector witness = disc?.Witness.Middle ?? default;
        return new(face.Static, face.ElementId, true, disc is not null, plane?.Middle ?? double.NaN,
            disc?.Maximum.Middle ?? double.NaN, face.Normal,
            disc is not null ? [new(SupportWitnessRole.Footprint, witness, face.Normal, face.Normal.Y)] : [], disc is not null)
        { Face = face, PlaneBounds = plane, MaximumBounds = disc?.Maximum };
    }

    internal static FootprintOracleMember? Curved(OracleTangent part, in SupportFootprintRequest request)
    {
        double axisX = request.Axis.X, axisZ = request.Axis.Y, discRadius = request.Radius;
        (double bottom, double top) = Volume(request);
        double radius = (double)request.Radius + request.ContactBand;
        // Check the raw surface, including context-only sides and downward caps, against the whole column.
        if (!SurfaceMeetsVolume(part, request.Axis.X, request.Axis.Y, radius, bottom, top)) return null;
        var witnesses = new List<FootprintOracleWitness>();
        SurfacePoint? axis = UpperAt(part, request.Axis.X, request.Axis.Y);
        if (axis is { } a) witnesses.Add(new(SupportWitnessRole.Axis, a.Position, a.Normal, a.Normal.Y));
        SurfacePoint? highest = Maximum(part, request.Axis.X, request.Axis.Y, request.Radius, 0);
        if (highest is { } h)
        {
            SurfacePoint selected = h;
            if (h.Normal.Y < request.CosMaxSlope &&
                Maximum(part, request.Axis.X, request.Axis.Y, request.Radius, request.CosMaxSlope) is { } walkable)
                selected = walkable;
            witnesses.Add(new(SupportWitnessRole.Footprint, selected.Position, selected.Normal, selected.Normal.Y));
        }
        SurfacePoint? own = witnesses.Count == 0 ? null : new(witnesses[^1].Position, witnesses[^1].Normal);
        return new(part.Static, part.ElementId, true, highest is not null,
            own is { } p && p.Normal.Y > 0 ? Plane(p.Position, p.Normal, request.Axis.X, request.Axis.Y) : double.NaN,
            highest?.Position.Y ?? double.NaN, own?.Normal ?? default, witnesses,
            witnesses.All(w => InDisc(w.Position, axisX, axisZ, discRadius)));
    }

    internal static FootprintOracleSupport Select(IReadOnlyList<FootprintOracleMember> members,
        in SupportFootprintRequest request)
    {
        if (!(request.CosMaxSlope > 0) || members.Count(m => m.Admitted) > 256)
            return Empty(SupportStatus.Refused);
        FootprintOracleSupport best = Empty(SupportStatus.None);
        foreach (FootprintOracleMember member in members)
        {
            if (!member.Admitted || !member.HasDiscMaximum) continue;
            foreach (FootprintOracleWitness witness in member.Witnesses)
            {
                if (!(witness.Normal.Y > 0)) continue;
                bool walkable = witness.UpwardLower >= request.CosMaxSlope;
                double plane = Plane(witness.Position, witness.Normal, request.Axis.X, request.Axis.Y);
                OracleVector normal = witness.Normal;
                bool suppressed = false;
                if (member.Face is { } face)
                    foreach (FootprintOracleMember other in members)
                    {
                        if (ReferenceEquals(member, other) || !other.Admitted || other.Face is not { } joined ||
                            !(joined.Normal.Y > 0) || !Joined(face, joined, request.ContactBand)) continue;
                        bool otherWalkable = joined.Normal.Y >= request.CosMaxSlope;
                        if (!walkable && otherWalkable) { suppressed = true; break; }
                        if (walkable == otherWalkable && other.Plane < plane)
                            (plane, normal) = (other.Plane, other.Normal);
                    }
                double height = Math.Min(plane, member.Face is null ? witness.Position.Y : member.Maximum);
                if (suppressed || height < request.BandLower || height > request.BandUpper) continue;
                var candidate = new FootprintOracleSupport(walkable ? SupportStatus.Walkable : SupportStatus.Steep,
                    height, Math.BitDecrement(height), Math.BitIncrement(height), normal, witness.Position,
                    member.Static, member.ElementId);
                if (best.Status == SupportStatus.None || Beats(candidate, best, request)) best = candidate;
            }
        }
        return best;
    }

    static FootprintOracleSupport Empty(SupportStatus status) =>
        new(status, double.NaN, double.NaN, double.NaN, default, default, null, -1);

    static bool Beats(FootprintOracleSupport a, FootprintOracleSupport b, in SupportFootprintRequest request)
    {
        if (a.Height != b.Height) return a.Height > b.Height;
        if (a.Status != b.Status) return a.Status == SupportStatus.Walkable;
        double da = DistanceSquared(a.Witness, request.Axis.X, request.Axis.Y);
        double db = DistanceSquared(b.Witness, request.Axis.X, request.Axis.Y);
        if (da != db) return da < db;
        if (a.Static is null) return b.Static is not null;
        return b.Static is { } owner && a.Static.Value.Value < owner.Value;
    }

    internal static bool Joined(OracleFace a, OracleFace b, double band)
    {
        if (a.Vertices.Any(v => OracleVector.Dot(b.Normal, v - b.Vertices[0]) > band) ||
            b.Vertices.Any(v => OracleVector.Dot(a.Normal, v - a.Vertices[0]) > band)) return false;
        foreach ((OracleFace edges, OracleFace polygon) in new[] { (a, b), (b, a) })
            for (int i = 0; i < edges.Vertices.Length; i++)
                if (SupportNeighborhoodOracle.Contact(polygon, edges.Vertices[i],
                        edges.Vertices[(i + 1) % edges.Vertices.Length], 0).Separation <= band) return true;
        return false;
    }

    static double Plane(OracleVector point, OracleVector normal, double x, double z) =>
        point.Y + (normal.X * (point.X - x) + normal.Z * (point.Z - z)) / normal.Y;
    static double DistanceSquared(OracleVector p, double x, double z) =>
        (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z);
    static bool InDisc(OracleVector p, double x, double z, double r) => DistanceSquared(p, x, z) <= r * r;

    readonly record struct SurfacePoint(OracleVector Position, OracleVector Normal);

    static SurfacePoint? Maximum(OracleTangent part, double x, double z, double r, double slope)
    {
        var candidates = new List<SurfacePoint>();
        if (part.Surface == OracleSurface.Round)
        {
            foreach (double end in part.HalfLength == 0 ? new[] { 0.0 } : new[] { -part.HalfLength, part.HalfLength })
            {
                OracleVector centre = part.Centre + part.Axis * end;
                double dx = x - centre.X, dz = z - centre.Z, distance = Math.Sqrt(dx * dx + dz * dz);
                double fraction = distance <= r ? 0 : (distance - r) / distance;
                double qx = dx * fraction, qz = dz * fraction, square = part.Radius * part.Radius - qx * qx - qz * qz;
                if (square < 0) continue;
                OracleVector radial = new(qx, Math.Sqrt(square), qz), normal = radial * (1 / part.Radius);
                double along = OracleVector.Dot(normal, part.Axis);
                if (normal.Y >= 0 && normal.Y >= slope &&
                    (part.HalfLength == 0 || (end > 0 ? along >= 0 : along <= 0)))
                    candidates.Add(new(centre + radial, normal));
            }
            if (part.HalfLength > 0) candidates.AddRange(SideCandidates(part, x, z, r, slope));
        }
        else if (part.Surface == OracleSurface.CylinderSide)
            candidates.AddRange(SideCandidates(part, x, z, r, slope));
        else candidates.AddRange(CapCandidates(part, x, z, r, slope));
        return candidates.Count == 0 ? null : candidates.MaxBy(p => p.Position.Y);
    }

    // s = sin(theta) in the upper normal semicircle. Height is concave on each feasible interval:
    // min(cy + |ay|*half + R*u*sqrt(1-s*s),
    //      cy - ay*parallel/u + (R*sqrt(1-s*s) + |ay|*sqrt(r*r-perp(s)^2))/u).
    // Circle/endpoint intersections give quartic boundary roots, so a narrow feasible arc cannot be skipped.
    static List<SurfacePoint> SideCandidates(OracleTangent part, double x, double z, double r, double slope)
    {
        var result = new List<SurfacePoint>();
        OracleVector axis = part.Axis.Y < 0 ? part.Axis * -1 : part.Axis;
        double u = Math.Sqrt(axis.X * axis.X + axis.Z * axis.Z), ay = axis.Y, radius = part.Radius;
        if (u == 0)
        {
            if (slope > 0) return result;
            double dx = x - part.Centre.X, dz = z - part.Centre.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            if (Math.Abs(distance - radius) > r) return result;
            OracleVector normal = distance == 0 ? new(1, 0, 0) : new(dx / distance, 0, dz / distance);
            result.Add(new(part.Centre + axis * part.HalfLength + normal * radius, normal));
            return result;
        }
        if (slope > u) return result;
        if (ay == 0)
        {
            if (SupportFootprintHorizontalSideOracle.Maximum(part, x, z, r, slope) is { } point)
                result.Add(new(point.Position, point.Normal));
            return result;
        }
        OracleVector horizontal = new(axis.X / u, 0, axis.Z / u);
        OracleVector across = new(-horizontal.Z, 0, horizontal.X);
        OracleVector up = new(-ay * horizontal.X, u, -ay * horizontal.Z);
        OracleVector delta = part.Centre - new OracleVector(x, part.Centre.Y, z);
        double parallel = OracleVector.Dot(delta, horizontal), perpendicular = OracleVector.Dot(delta, across);
        double limit = Math.Sqrt(Math.Max(0, 1 - slope * slope / (u * u)));
        double low = Math.Max(-limit, (-r - perpendicular) / radius);
        double high = Math.Min(limit, (r - perpendicular) / radius);
        if (low > high) return result;
        var cuts = new List<double> { low, high };
        foreach (double end in new[] { -part.HalfLength, part.HalfLength })
        {
            double p = parallel + end * u;
            double a = p * p + perpendicular * perpendicular + radius * radius * ay * ay - r * r;
            double b = 2 * radius * perpendicular, c = radius * radius * u * u, d = 2 * radius * ay * p;
            cuts.AddRange(Roots([a * a - d * d, 2 * a * b, b * b + 2 * a * c + d * d,
                2 * b * c, c * c], low, high));
        }
        cuts.Sort();
        SurfacePoint? At(double s)
        {
            double cosine = Math.Sqrt(Math.Max(0, 1 - s * s));
            OracleVector normal = up * cosine + across * s;
            if (!(normal.Y >= 0) || normal.Y < slope) return null;
            double perp = perpendicular + radius * s, square = r * r - perp * perp;
            if (square < 0) return null;
            double centre = -(parallel - radius * ay * cosine) / u, span = Math.Sqrt(square) / u;
            double tLow = Math.Max(-part.HalfLength, centre - span);
            double tHigh = Math.Min(part.HalfLength, centre + span);
            if (tLow > tHigh) return null;
            double t = ay == 0 ? Math.Clamp(centre, tLow, tHigh) : tHigh;
            OracleVector position = part.Centre + axis * t + normal * radius;
            while (!InDisc(position, x, z, r) && t != centre)
            {
                double next = t > centre ? Math.BitDecrement(t) : Math.BitIncrement(t);
                if (next < tLow || next > tHigh || next == t) return null;
                t = next;
                position = part.Centre + axis * t + normal * radius;
            }
            return InDisc(position, x, z, r) ? new(position, normal) : null;
        }
        foreach (double cut in cuts) if (At(cut) is { } point) result.Add(point);
        for (int i = 1; i < cuts.Count; i++)
        {
            double a = cuts[i - 1], b = cuts[i];
            if (At((a + b) / 2) is null) continue;
            double s = ArgMaximum(t => At(t)?.Position.Y ?? double.NegativeInfinity, a, b);
            if (At(s) is { } point) result.Add(point);
        }
        return result;
    }

    static List<SurfacePoint> CapCandidates(OracleTangent part, double x, double z, double r, double slope)
    {
        var result = new List<SurfacePoint>();
        double side = part.Surface == OracleSurface.CylinderTop ? 1 : -1;
        OracleVector normal = part.Axis * side, centre = part.Centre + part.Axis * (side * part.HalfLength);
        if (normal.Y == 0 && slope == 0)
        {
            OracleVector capAcross = new(-normal.Z, 0, normal.X), delta = centre - new OracleVector(x, centre.Y, z);
            double along = OracleVector.Dot(delta, capAcross), offset = OracleVector.Dot(delta, normal);
            double square = r * r - offset * offset;
            if (square < 0) return result;
            double low = Math.Max(-part.Radius, -along - Math.Sqrt(square));
            double high = Math.Min(part.Radius, -along + Math.Sqrt(square));
            if (low <= high)
            {
                double nearest = Math.Clamp(0, low, high);
                result.Add(new(centre + capAcross * nearest + new OracleVector(0,
                    Math.Sqrt(part.Radius * part.Radius - nearest * nearest), 0), normal));
            }
            return result;
        }
        if (!(normal.Y > 0) || normal.Y < slope) return result;
        double horizontal = Math.Sqrt(normal.X * normal.X + normal.Z * normal.Z);
        OracleVector up = horizontal == 0 ? new(1, 0, 0) :
            new OracleVector(-normal.Y * normal.X / horizontal, horizontal, -normal.Y * normal.Z / horizontal);
        OracleVector across = horizontal == 0 ? new(0, 0, 1) :
            new OracleVector(-normal.Z / horizontal, 0, normal.X / horizontal);
        void Add(OracleVector p)
        {
            if (InDisc(p, x, z, r)) result.Add(new(p, normal));
        }
        Add(centre);
        Add(centre + up * part.Radius);
        double px = -normal.X / normal.Y, pz = -normal.Z / normal.Y, length = Math.Sqrt(px * px + pz * pz);
        foreach (OracleVector point in new[] { new OracleVector(x, Plane(centre, normal, x, z), z),
                     new OracleVector(x + (length == 0 ? 0 : r * px / length),
                         Plane(centre, normal, x + (length == 0 ? 0 : r * px / length),
                             z + (length == 0 ? 0 : r * pz / length)), z + (length == 0 ? 0 : r * pz / length)) })
            if ((point - centre).Length <= part.Radius && InDisc(point, x, z, r)) result.Add(new(point, normal));
        double[] X = [centre.X - x + part.Radius * up.X, 2 * part.Radius * across.X,
            centre.X - x - part.Radius * up.X];
        double[] Z = [centre.Z - z + part.Radius * up.Z, 2 * part.Radius * across.Z,
            centre.Z - z - part.Radius * up.Z];
        double[] polynomial = new double[5];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) polynomial[i + j] += X[i] * X[j] + Z[i] * Z[j];
        polynomial[0] -= r * r;
        polynomial[2] -= 2 * r * r;
        polynomial[4] -= r * r;
        double bound = RootBound(polynomial);
        OracleVector At(double angle) => centre + (up * Math.Cos(angle) + across * Math.Sin(angle)) * part.Radius;
        double[] angles = Roots(polynomial, -bound, bound).Select(root => 2 * Math.Atan(root))
            .Append(Math.PI).Order().ToArray();
        foreach (double theta in angles)
        {
            Add(At(theta));
            Add(At(Math.BitDecrement(theta)));
            Add(At(Math.BitIncrement(theta)));
        }
        // The root polynomial and the final trigonometric map round differently. Approach every boundary
        // from each feasible arc, rather than losing its maximum when three rounded endpoints lie outside D.
        for (int i = 0; i < angles.Length; i++)
        {
            double a = angles[i], b = i + 1 < angles.Length ? angles[i + 1] : angles[0] + 2 * Math.PI;
            double middle = (a + b) / 2;
            if (!InDisc(At(middle), x, z, r)) continue;
            Add(At(middle));
            foreach (double boundary in new[] { a, b })
            {
                double inside = middle, outside = boundary;
                while (!InDisc(At(outside), x, z, r))
                {
                    double next = inside + (outside - inside) / 2;
                    if (next == inside || next == outside) break;
                    if (InDisc(At(next), x, z, r)) inside = next;
                    else outside = next;
                }
                Add(At(InDisc(At(outside), x, z, r) ? outside : inside));
            }
        }
        Add(centre - up * part.Radius);
        return result;
    }

    static double ArgMaximum(Func<double, double> f, double a, double b)
    {
        double ratio = (Math.Sqrt(5) - 1) / 2;
        double c = b - ratio * (b - a), d = a + ratio * (b - a), fc = f(c), fd = f(d);
        for (int i = 0; i < 96 && a < c && c < d && d < b; i++)
        {
            if (fc >= fd) { b = d; d = c; fd = fc; c = b - ratio * (b - a); fc = f(c); }
            else { a = c; c = d; fc = fd; d = a + ratio * (b - a); fd = f(d); }
        }
        return fc >= fd ? c : d;
    }

    // Derivative roots isolate every real polynomial root, including even multiplicities. No scan grid.
    static List<double> Roots(double[] coefficients, double low, double high)
    {
        int degree = coefficients.Length - 1;
        while (degree > 0 && coefficients[degree] == 0) degree--;
        var roots = new List<double>();
        if (degree == 0) return roots;
        if (degree == 1)
        {
            double root = -coefficients[0] / coefficients[1];
            if (root >= low && root <= high) roots.Add(root);
            return roots;
        }
        var derivative = new double[degree];
        for (int i = 1; i <= degree; i++) derivative[i - 1] = i * coefficients[i];
        var cuts = Roots(derivative, low, high);
        cuts.Insert(0, low);
        cuts.Add(high);
        double Value(double x)
        {
            double result = coefficients[degree];
            for (int i = degree - 1; i >= 0; i--) result = result * x + coefficients[i];
            return result;
        }
        foreach (double cut in cuts) if (Value(cut) == 0) roots.Add(cut);
        for (int i = 1; i < cuts.Count; i++)
        {
            double a = cuts[i - 1], b = cuts[i], fa = Value(a), fb = Value(b);
            if (fa == 0 || fb == 0 || Math.Sign(fa) == Math.Sign(fb)) continue;
            for (int k = 0; k < 128; k++)
            {
                double middle = a + (b - a) / 2;
                if (middle == a || middle == b) break;
                double fm = Value(middle);
                if (fm == 0) { a = b = middle; break; }
                if (Math.Sign(fm) == Math.Sign(fa)) { a = middle; fa = fm; }
                else b = middle;
            }
            roots.Add(a + (b - a) / 2);
        }
        roots.Sort();
        return roots;
    }

    static double RootBound(double[] polynomial)
    {
        int degree = polynomial.Length - 1;
        while (degree > 0 && polynomial[degree] == 0) degree--;
        double maximum = 0;
        for (int i = 0; i < degree; i++) maximum = Math.Max(maximum, Math.Abs(polynomial[i] / polynomial[degree]));
        return 1 + maximum;
    }

    static SurfacePoint? UpperAt(OracleTangent part, double x, double z)
    {
        var candidates = new List<SurfacePoint>();
        if (part.Surface == OracleSurface.Round)
            foreach (double end in part.HalfLength == 0 ? new[] { 0.0 } : new[] { -part.HalfLength, part.HalfLength })
            {
                OracleVector centre = part.Centre + part.Axis * end;
                double dx = x - centre.X, dz = z - centre.Z, square = part.Radius * part.Radius - dx * dx - dz * dz;
                if (square < 0) continue;
                OracleVector normal = new OracleVector(dx, Math.Sqrt(square), dz) * (1 / part.Radius);
                double along = OracleVector.Dot(normal, part.Axis);
                if (normal.Y >= 0 && (part.HalfLength == 0 || (end > 0 ? along >= 0 : along <= 0)))
                    candidates.Add(new(new(x, centre.Y + Math.Sqrt(square), z), normal));
            }
        if (part.Surface == OracleSurface.CylinderSide || (part.Surface == OracleSurface.Round && part.HalfLength > 0))
        {
            double dx = x - part.Centre.X, dz = z - part.Centre.Z, ay = part.Axis.Y;
            double a = part.Axis.X * part.Axis.X + part.Axis.Z * part.Axis.Z;
            double horizontalDot = dx * part.Axis.X + dz * part.Axis.Z;
            double b = -2 * ay * horizontalDot, c = dx * dx + dz * dz - horizontalDot * horizontalDot - part.Radius * part.Radius;
            double discriminant = b * b - 4 * a * c;
            if (a == 0 && c == 0)
                candidates.Add(new(new(x, part.Centre.Y + part.HalfLength, z),
                    new(dx / part.Radius, 0, dz / part.Radius)));
            if (a > 0 && discriminant >= 0)
            {
                double y = (-b + Math.Sqrt(discriminant)) / (2 * a), t = horizontalDot + ay * y;
                OracleVector normal = (new OracleVector(dx, y, dz) - part.Axis * t) * (1 / part.Radius);
                if (Math.Abs(t) <= part.HalfLength && normal.Y >= 0)
                    candidates.Add(new(new(x, part.Centre.Y + y, z), normal));
            }
        }
        if (part.Surface is OracleSurface.CylinderTop or OracleSurface.CylinderBottom)
        {
            double side = part.Surface == OracleSurface.CylinderTop ? 1 : -1;
            OracleVector normal = part.Axis * side, centre = part.Centre + part.Axis * (side * part.HalfLength);
            if (normal.Y == 0)
            {
                OracleVector delta = new OracleVector(x, centre.Y, z) - centre;
                double along = OracleVector.Dot(delta, new OracleVector(-normal.Z, 0, normal.X));
                if (OracleVector.Dot(delta, normal) == 0 && Math.Abs(along) <= part.Radius)
                    candidates.Add(new(new(x, centre.Y + Math.Sqrt(part.Radius * part.Radius - along * along), z), normal));
            }
            if (normal.Y > 0)
            {
                OracleVector point = new(x, Plane(centre, normal, x, z), z);
                if ((point - centre).Length <= part.Radius) candidates.Add(new(point, normal));
            }
        }
        return candidates.Count == 0 ? null : candidates.MaxBy(p => p.Position.Y);
    }

    static bool SurfaceMeetsVolume(OracleTangent part, double x, double z, double r, double bottom, double top)
    {
        if (part.Surface == OracleSurface.Round)
        {
            double DistanceToVolume(double t)
            {
                OracleVector core = part.Centre + part.Axis * t;
                double radial = Math.Max(Math.Sqrt(DistanceSquared(core, x, z)) - r, 0);
                double vertical = Math.Max(Math.Max(bottom - core.Y, core.Y - top), 0);
                return radial * radial + vertical * vertical;
            }
            double best = part.HalfLength == 0 ? DistanceToVolume(0) :
                Math.Min(Math.Min(DistanceToVolume(-part.HalfLength), DistanceToVolume(part.HalfLength)),
                    DistanceToVolume(ArgMaximum(t => -DistanceToVolume(t), -part.HalfLength, part.HalfLength)));
            if (best > part.Radius * part.Radius) return false;
            // Distance to a segment is convex. Its maximum over a closed cylinder is on one circular rim.
            double maximum = 0;
            foreach (double y in new[] { bottom, top })
            {
                OracleVector delta = new(x - part.Centre.X, y - part.Centre.Y, z - part.Centre.Z);
                double k = OracleVector.Dot(delta, part.Axis);
                double[] vx = [delta.X + r, 0, delta.X - r], vy = [delta.Y, 0, delta.Y], vz = [delta.Z, 2 * r, delta.Z];
                double[] dot = [k + r * part.Axis.X, 2 * r * part.Axis.Z, k - r * part.Axis.X];
                double[] norm = Sum(Product(vx, vx), Product(vy, vy), Product(vz, vz));
                var cuts = new List<double> { 0 };
                foreach (double end in new[] { -part.HalfLength, part.HalfLength })
                    cuts.AddRange(Roots([dot[0] - end, dot[1], dot[2] - end], -RootBound(dot), RootBound(dot)));
                foreach (double? end in new double?[] { null, -part.HalfLength, part.HalfLength })
                {
                    double[] numerator = end is { } t ? Sum(norm,
                        Product([1, 0, 1], Sum(Product([t * t], [1, 0, 1]), Product([-2 * t], dot))))
                        : Sum(norm, Product([-1], Product(dot, dot)));
                    double[] derivative = new double[numerator.Length - 1];
                    for (int i = 1; i < numerator.Length; i++) derivative[i - 1] = i * numerator[i];
                    double[] stationary = Sum(Product(derivative, [1, 0, 1]), Product(numerator, [0, -4]));
                    double bound = RootBound(stationary);
                    cuts.AddRange(Roots(stationary, -bound, bound));
                }
                foreach (double theta in cuts.Select(t => 2 * Math.Atan(t)).Append(Math.PI))
                {
                    OracleVector point = delta + new OracleVector(r * Math.Cos(theta), 0, r * Math.Sin(theta));
                    double t = Math.Clamp(OracleVector.Dot(point, part.Axis), -part.HalfLength, part.HalfLength);
                    OracleVector radial = point - part.Axis * t;
                    maximum = Math.Max(maximum, OracleVector.Dot(radial, radial));
                }
            }
            return maximum >= part.Radius * part.Radius;
        }
        if (part.Surface == OracleSurface.CylinderSide)
            return SideMeetsVolume(part, x, z, r, bottom, top);
        double sign = part.Surface == OracleSurface.CylinderTop ? 1 : -1;
        OracleVector centreCap = part.Centre + part.Axis * (sign * part.HalfLength);
        if (part.Axis.Y == 0)
        {
            OracleVector across = new(-part.Axis.Z, 0, part.Axis.X);
            OracleVector delta = centreCap - new OracleVector(x, centreCap.Y, z);
            double perpendicular = OracleVector.Dot(delta, part.Axis), along = OracleVector.Dot(delta, across);
            double square = r * r - perpendicular * perpendicular;
            if (square < 0) return false;
            double nearest = Math.Clamp(0, -along - Math.Sqrt(square), -along + Math.Sqrt(square));
            if (Math.Abs(nearest) > part.Radius) return false;
            double height = Math.Sqrt(part.Radius * part.Radius - nearest * nearest);
            return centreCap.Y + height >= bottom && centreCap.Y - height <= top;
        }
        // Represent the same geometric disk with an upward normal, without moving its centre.
        OracleTangent upper = part with
        {
            Surface = OracleSurface.CylinderTop,
            Centre = centreCap,
            Axis = part.Axis * Math.Sign(part.Axis.Y),
            HalfLength = 0
        };
        SurfacePoint? max = Maximum(upper, x, z, r, 0);
        OracleTangent reflected = Reflect(upper);
        reflected = reflected with { Axis = reflected.Axis * -1 };
        SurfacePoint? min = Maximum(reflected, x, z, r, 0);
        return max is { } high && min is { } low && high.Position.Y >= bottom && -low.Position.Y <= top;
    }

    static OracleTangent Reflect(OracleTangent part) => part with
    {
        Centre = new(part.Centre.X, -part.Centre.Y, part.Centre.Z),
        Axis = new(part.Axis.X, -part.Axis.Y, part.Axis.Z),
    };

    static bool SideMeetsVolume(OracleTangent part, double x, double z, double r, double bottom, double top)
    {
        OracleVector axis = part.Axis.Y < 0 ? part.Axis * -1 : part.Axis;
        double u = Math.Sqrt(axis.X * axis.X + axis.Z * axis.Z), ay = axis.Y, radius = part.Radius;
        if (u == 0)
        {
            double distance = Math.Sqrt(DistanceSquared(part.Centre, x, z));
            return Math.Max(distance - r, 0) <= radius && distance + r >= radius &&
                part.Centre.Y + part.HalfLength >= bottom && part.Centre.Y - part.HalfLength <= top;
        }
        OracleVector horizontal = new(axis.X / u, 0, axis.Z / u), across = new(-axis.Z / u, 0, axis.X / u);
        OracleVector delta = part.Centre - new OracleVector(x, part.Centre.Y, z);
        double dp = OracleVector.Dot(delta, horizontal), dv = OracleVector.Dot(delta, across);
        double low = Math.Max(-1, (-r - dv) / radius), high = Math.Min(1, (r - dv) / radius);
        if (low > high) return false;
        var cuts = new List<double> { low, high };
        void Boundary(double p, double k)
        {
            double a = p * p + dv * dv + k * k - r * r, b = 2 * radius * dv, c = radius * radius - k * k, d = 2 * p * k;
            cuts.AddRange(Roots([a * a - d * d, 2 * a * b, b * b + 2 * a * c + d * d, 2 * b * c, c * c], low, high));
        }
        foreach (double t in new[] { -part.HalfLength, part.HalfLength }) Boundary(dp + t * u, radius * ay);
        if (ay != 0) foreach (double y in new[] { bottom, top }) Boundary(dp + u * (y - part.Centre.Y) / ay, radius / ay);
        else foreach (double y in new[] { bottom, top })
        {
            double cosine = (y - part.Centre.Y) / (radius * u);
            if (Math.Abs(cosine) <= 1) cuts.AddRange([Math.Sqrt(1 - cosine * cosine), -Math.Sqrt(1 - cosine * cosine)]);
        }
        cuts.Sort();
        var points = new List<double>(cuts);
        for (int i = 1; i < cuts.Count; i++) points.Add((cuts[i - 1] + cuts[i]) / 2);
        foreach (double s in points.Where(s => s >= low && s <= high))
            foreach (double sign in new[] { -1.0, 1.0 })
            {
                double cosine = sign * Math.Sqrt(Math.Max(0, 1 - s * s)), perp = dv + radius * s;
                double square = r * r - perp * perp;
                if (square < 0) continue;
                double t = -(dp - radius * ay * cosine) / u, span = Math.Sqrt(square) / u;
                double a = Math.Max(-part.HalfLength, t - span), b = Math.Min(part.HalfLength, t + span);
                double y = part.Centre.Y + radius * u * cosine;
                if (ay == 0) { if (a <= b && y >= bottom && y <= top) return true; }
                else if (Math.Max(a, (bottom - y) / ay) <= Math.Min(b, (top - y) / ay)) return true;
            }
        return false;
    }

    static double[] Product(double[] a, double[] b)
    {
        var result = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++) for (int j = 0; j < b.Length; j++) result[i + j] += a[i] * b[j];
        return result;
    }
    static double[] Sum(params double[][] terms)
    {
        var result = new double[terms.Max(t => t.Length)];
        foreach (double[] term in terms) for (int i = 0; i < term.Length; i++) result[i] += term[i];
        return result;
    }

    /// <summary>Bounds the alternate per-point reading independently. Adaptive interval subdivision retains an
    /// upper bound for every surviving cell and attained samples give the lower bound. The 1e-7 m stop only sizes
    /// this test oracle's reported enclosure, never production reach, admission or classification.</summary>
    internal static (double Lower, double Upper) PerPointSupremum(OracleTangent part, in SupportFootprintRequest request)
    {
        SupportFootprintRequest query = request;
        if (part.Surface == OracleSurface.Round && part.HalfLength == 0)
            return SphereReading(part.Centre, part.Radius, query);
        if (part.Surface == OracleSurface.CylinderSide) return SideReading(part, query);
        (double Lower, double Upper) side = part.Surface == OracleSurface.Round
            ? SideReading(part with { Surface = OracleSurface.CylinderSide }, query)
            : (double.NegativeInfinity, double.NegativeInfinity);
        SurfacePoint? maximum = Maximum(part, query.Axis.X, query.Axis.Y, query.Radius, 0);
        if (maximum is null) return (double.NegativeInfinity, double.NegativeInfinity);
        double lower = side.Lower;
        void Sample(double x, double z)
        {
            if (!InDisc(new(x, 0, z), query.Axis.X, query.Axis.Y, query.Radius) ||
                UpperAt(part, x, z) is not { } p || !(p.Normal.Y > 0)) return;
            lower = Math.Max(lower, SupportFootprintCurvedProofOracle.RoundSampleLower(part, x, z, query));
        }
        Sample(maximum.Value.Position.X, maximum.Value.Position.Z);
        Sample(query.Axis.X, query.Axis.Y);
        if (part.Surface is OracleSurface.CylinderTop or OracleSurface.CylinderBottom)
        {
            if (!(maximum.Value.Normal.Y > 0)) return (double.NegativeInfinity, double.NegativeInfinity);
            if (SupportFootprintCurvedProofOracle.CapReading(part, query) is { } certified) return certified;
            double height = Math.Min(maximum.Value.Position.Y,
                Plane(maximum.Value.Position, maximum.Value.Normal, query.Axis.X, query.Axis.Y));
            return (Math.BitDecrement(height), Math.BitIncrement(height));
        }
        var queue = new PriorityQueue<(Range X, Range Z, double Upper), double>();
        void Enqueue(Range x, Range z)
        {
            double dx = Math.Max(Math.Max(x.Low - query.Axis.X, query.Axis.X - x.High), 0);
            double dz = Math.Max(Math.Max(z.Low - query.Axis.Y, query.Axis.Y - z.High), 0);
            if (dx * dx + dz * dz > (double)query.Radius * query.Radius) return;
            Sample(x.Middle, z.Middle);
            double upper = PointUpper(part, x, z, query);
            if (upper > lower) queue.Enqueue((x, z, upper), -upper);
        }
        Enqueue(new((double)query.Axis.X - query.Radius, (double)query.Axis.X + query.Radius),
            new((double)query.Axis.Y - query.Radius, (double)query.Axis.Y + query.Radius));
        while (queue.TryPeek(out var next, out _) && next.Upper - lower > 1e-7)
        {
            queue.Dequeue();
            if (next.X.High - next.X.Low >= next.Z.High - next.Z.Low)
            {
                Enqueue(new(next.X.Low, next.X.Middle), next.Z);
                Enqueue(new(next.X.Middle, next.X.High), next.Z);
            }
            else
            {
                Enqueue(next.X, new(next.Z.Low, next.Z.Middle));
                Enqueue(next.X, new(next.Z.Middle, next.Z.High));
            }
        }
        return (lower, Math.Max(side.Upper, queue.TryPeek(out var last, out _) ? Math.Max(lower, last.Upper) : lower));
    }

    static double PointUpper(OracleTangent part, Range x, Range z, in SupportFootprintRequest request)
    {
        double ax = request.Axis.X, az = request.Axis.Y;
        double upper = double.NegativeInfinity, radius = part.Radius;
        void Offer(Range y, Range nx, Range ny, Range nz, double cap)
        {
            if (ny.High <= 0) return;
            Range plane = y + (nx * (x - ax) + nz * (z - az)) / ny;
            upper = Math.Max(upper, Math.Min(cap, Math.Min(y.High, plane.High)));
        }
        if (part.Surface == OracleSurface.Round)
            foreach (double end in part.HalfLength == 0 ? new[] { 0.0 } : new[] { -part.HalfLength, part.HalfLength })
            {
                OracleVector centre = part.Centre + part.Axis * end;
                Range dx = x - centre.X, dz = z - centre.Z, square = radius * radius - dx.Square - dz.Square;
                if (square.High < 0) continue;
                Range dy = square.Sqrt, along = (dx * part.Axis.X + dy * part.Axis.Y + dz * part.Axis.Z) / radius;
                if (part.HalfLength != 0 && (end > 0 ? along.High < 0 : along.Low > 0)) continue;
                Offer(centre.Y + dy, dx / radius, dy / radius, dz / radius, SphereReading(centre, radius, request).Upper);
            }
        return upper;
    }

    // At fixed radial distance rho on a sphere, h is constant and tangent height falls with axis dot radial.
    // The disc bounds that dot from below. The resulting min(tangent,h) decreases away from the nearest rho.
    static (double Lower, double Upper) SphereReading(OracleVector centre, double radius, in SupportFootprintRequest request)
    {
        double nearest = Math.Max(Math.Sqrt(DistanceSquared(centre, request.Axis.X, request.Axis.Y)) - request.Radius, 0);
        if (nearest >= radius) return (double.NegativeInfinity, double.NegativeInfinity);
        Range dx = (Range)request.Axis.X - centre.X, dz = (Range)request.Axis.Y - centre.Z;
        Range distance = (dx.Square + dz.Square).Sqrt, rho = distance - request.Radius;
        rho = new(Math.Max(0, rho.Low), Math.Max(0, rho.High));
        Range square = radius * radius - rho.Square;
        if (square.High <= 0) return (double.NegativeInfinity, double.NegativeInfinity);
        Range y = square.Sqrt, plane = (radius * radius - distance * rho) / y;
        return (Math.BitDecrement(centre.Y + Math.Min(y.Low, plane.Low)),
            Math.BitIncrement(centre.Y + Math.Min(y.High, plane.High)));
    }

    // On a finite cylinder side the tangent at the axis is independent of the axial coordinate t.
    // For each s=sin(theta), only the highest feasible t can maximize min(tangent,height).
    static (double Lower, double Upper) SideReading(OracleTangent part, in SupportFootprintRequest request)
    {
        SupportFootprintRequest query = request;
        OracleVector axis = part.Axis.Y < 0 ? part.Axis * -1 : part.Axis;
        double u = Math.Sqrt(axis.X * axis.X + axis.Z * axis.Z), ay = axis.Y, radius = part.Radius, r = query.Radius;
        if (u == 0) return (double.NegativeInfinity, double.NegativeInfinity);
        if (ay == 0) return SupportFootprintHorizontalSideOracle.PerPointSupremum(part, query);
        OracleVector parallel = new(axis.X / u, 0, axis.Z / u), across = new(-axis.Z / u, 0, axis.X / u);
        OracleVector delta = part.Centre - new OracleVector(query.Axis.X, part.Centre.Y, query.Axis.Y);
        double dp = OracleVector.Dot(delta, parallel), dv = OracleVector.Dot(delta, across);
        double low = Math.Max(-1, (-r - dv) / radius), high = Math.Min(1, (r - dv) / radius);
        if (low > high || low >= 1 || high <= -1) return (double.NegativeInfinity, double.NegativeInfinity);
        double lower = double.NegativeInfinity;
        SurfacePoint? maximum = Maximum(part, query.Axis.X, query.Axis.Y, r, 0);
        if (maximum is { } m && m.Normal.Y > 0)
            lower = Math.BitDecrement(Math.Min(m.Position.Y, Plane(m.Position, m.Normal, query.Axis.X, query.Axis.Y)));
        var queue = new PriorityQueue<(Range S, double Upper), double>();
        void Add(Range s)
        {
            double sample = s.Middle, cosine = Math.Sqrt(Math.Max(0, 1 - sample * sample));
            double perp = dv + radius * sample, square = r * r - perp * perp;
            if (square >= 0 && cosine > 0)
            {
                double tCentre = -(dp - radius * ay * cosine) / u, span = Math.Sqrt(square) / u;
                double tLow = Math.Max(-part.HalfLength, tCentre - span), tHigh = Math.Min(part.HalfLength, tCentre + span);
                if (tLow <= tHigh)
                {
                    double y = part.Centre.Y + ay * tHigh + radius * u * cosine;
                    double p = part.Centre.Y - ay * dp / u + (radius + dv * sample) / (u * cosine);
                    lower = Math.Max(lower, Math.BitDecrement(Math.Min(y, p)));
                }
            }
            Range cos = (1 - s.Square).Sqrt, perpendicular = dv + radius * s;
            Range radial = r * r - perpendicular.Square;
            if (radial.High < 0) return;
            Range extent = radial.Sqrt, centre = -(dp - radius * ay * cos) / u;
            if (centre.High + extent.High / u < -part.HalfLength || centre.Low - extent.High / u > part.HalfLength) return;
            Range cap = part.Centre.Y + ay * part.HalfLength + radius * u * cos;
            Range disc = part.Centre.Y - ay * dp / u + (radius * cos + ay * extent) / u;
            Range tangent = part.Centre.Y - ay * dp / u + (radius + dv * s) / (u * cos);
            double upper = Math.Min(tangent.High, Math.Min(cap.High, disc.High));
            if (upper > lower) queue.Enqueue((s, upper), -upper);
        }
        Add(new(low, high));
        while (queue.TryPeek(out var next, out _) && next.Upper - lower > 1e-7)
        {
            double middle = next.S.Middle;
            // Keep the outward upper bound when no interior midpoint is representable.
            if (!(next.S.Low < middle && middle < next.S.High)) break;
            queue.Dequeue();
            Add(new(next.S.Low, middle));
            Add(new(middle, next.S.High));
        }
        return (lower, queue.TryPeek(out var last, out _) ? Math.Max(lower, last.Upper) : lower);
    }

    internal readonly record struct Range(double Low, double High)
    {
        internal double Middle => Low + (High - Low) / 2;
        internal Range Square => new(Low <= 0 && High >= 0 ? 0 : Math.BitDecrement(Math.Min(Low * Low, High * High)),
            Math.BitIncrement(Math.Max(Low * Low, High * High)));
        internal Range Sqrt => new(Math.BitDecrement(Math.Sqrt(Math.Max(0, Low))), Math.BitIncrement(Math.Sqrt(Math.Max(0, High))));
        public static implicit operator Range(double value) => new(value, value);
        public static Range operator +(Range a, Range b) => new(Math.BitDecrement(a.Low + b.Low), Math.BitIncrement(a.High + b.High));
        public static Range operator -(Range a) => new(-a.High, -a.Low);
        public static Range operator -(Range a, Range b) => a + -b;
        public static Range operator *(Range a, Range b)
        {
            double[] products = [a.Low * b.Low, a.Low * b.High, a.High * b.Low, a.High * b.High];
            return new(Math.BitDecrement(products.Min()), Math.BitIncrement(products.Max()));
        }
        public static Range operator /(Range a, Range b) => b.Low <= 0 && b.High >= 0
            ? new(double.NegativeInfinity, double.PositiveInfinity)
            : a * new Range(Math.BitDecrement(1 / b.High), Math.BitIncrement(1 / b.Low));
    }
}
