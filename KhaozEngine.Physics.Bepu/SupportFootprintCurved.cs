using System;
using KhaozEngine.Physics;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Independent attained SG-19 roles for curved strata. Tilted parts use certified regions.</summary>
internal sealed partial class SupportFootprintCurved
{
    readonly SupportCurvedWork _work;
    internal SupportFootprintCurved(SupportCurvedWork work)
    {
        _work = work;
        _arithmetic = new(work);
        _tilted = new(work);
    }

    readonly SupportCurvedSourceArithmetic _arithmetic;
    readonly SupportTiltedCurved _tilted;
    internal void ResetStatistics() => _tilted.ResetStatistics();
    readonly record struct Point(Number X, Number Y, Number Z)
    {
        internal GeometryVector Bounds(SupportCurvedSourceArithmetic arithmetic) =>
            new(arithmetic.Bounds(X), arithmetic.Bounds(Y), arithmetic.Bounds(Z));
    }
    readonly record struct Witness(Point Position, Point Normal);
    Point _centre, _e, _f;
    Number _radius, _half, _p, _q, _distanceSquared;
    bool _upright, _sphere;
    int _axisSign;
    GeometryInterval Bounds(Number number) => _arithmetic.Bounds(number);
    Number C(double value) => _arithmetic.Constant(value);
    Number Add(Number a, Number b) => _arithmetic.Add(a, b);
    Number Sub(Number a, Number b) => _arithmetic.Subtract(a, b);
    Number Mul(Number a, Number b) => _arithmetic.Multiply(a, b);
    Number Div(Number a, Number b) => _arithmetic.Divide(a, b);
    Number Sq(Number a) => _arithmetic.Square(a);
    Number Root(Number a) => _arithmetic.Root(a);
    Number Neg(Number a) => _arithmetic.Negate(a);
    Number Abs(Number a) => _arithmetic.Abs(a);
    Number Max(Number a, Number b) => _arithmetic.Max(a, b);
    Number Min(Number a, Number b) => _arithmetic.Min(a, b);
    GeometrySign Sign(Number a) => _arithmetic.Sign(a);
    Point Add(Point a, Point b) => new(Add(a.X, b.X), Add(a.Y, b.Y), Add(a.Z, b.Z));
    Point Scale(Point a, Number b) => new(Mul(a.X, b), Mul(a.Y, b), Mul(a.Z, b));
    Point Up => new(C(0), C(1), C(0));

    bool Begin(in SupportCurvedLeaf leaf, double x, double z)
    {
        if (_work.Failed) return false;
        _arithmetic.Reset(leaf.SourceColumn);
        _centre = new(C(leaf.Centre.X.Lower), C(leaf.Centre.Y.Lower), C(leaf.Centre.Z.Lower));
        _radius = C(leaf.Radius); _half = C(leaf.HalfLength);
        _sphere = leaf.Shape == SupportCurvedShape.Sphere || (leaf.Shape == SupportCurvedShape.Capsule && leaf.HalfLength == 0);
        Number dx = Sub(C(x), _centre.X), dz = Sub(C(z), _centre.Z);
        _distanceSquared = Add(Sq(dx), Sq(dz));
        GeometrySign vertical = Sign(Add(Sq(_arithmetic.X), Sq(_arithmetic.Z)));
        _upright = vertical == GeometrySign.Zero;
        if (_sphere || _upright)
        {
            _axisSign = leaf.SourceColumn.Y < 0 ? -1 : 1;
            _p = C(0); _q = C(0); _e = Up; _f = new(C(1), C(0), C(0));
            return _work.Complete(true);
        }
        if (Sign(_arithmetic.Y) != GeometrySign.Zero || vertical != GeometrySign.Positive) return false;
        Number length = Root(Add(Sq(_arithmetic.X), Sq(_arithmetic.Z)));
        _e = new(Div(_arithmetic.X, length), C(0), Div(_arithmetic.Z, length));
        _f = new(Neg(_e.Z), C(0), _e.X);
        _p = Add(Mul(dx, _e.X), Mul(dz, _e.Z));
        _q = Add(Mul(dx, _f.X), Mul(dz, _f.Z));
        return _work.Complete(_e.Bounds(_arithmetic).IsResolved && Bounds(_p).IsResolved && Bounds(_q).IsResolved);
    }

    internal GeometrySign Measure(StaticHandle owner, in SupportCurvedLeaf leaf, int part,
        in SupportFootprintRequest request, out SupportFootprintMember member)
    {
        member = default;
        if (_work.Failed) return _work.Complete(GeometrySign.Unresolved, ref member);
        if (!SupportFootprintBounds.TryColumn(request, out double bottom, out double top)) return _work.Complete(GeometrySign.Unresolved, ref member);
        GeometryInterval reach = GeometryInterval.Exact(leaf.Radius).Add(GeometryInterval.Exact(leaf.HalfLength));
        GeometryInterval expanded = GeometryInterval.Exact(request.Radius).Add(GeometryInterval.Exact(request.ContactBand));
        GeometryInterval x = GeometryInterval.Exact(request.Axis.X), z = GeometryInterval.Exact(request.Axis.Y);
        if (leaf.Centre.X.Subtract(reach).Lower > x.Add(expanded).Upper ||
            leaf.Centre.X.Add(reach).Upper < x.Subtract(expanded).Lower ||
            leaf.Centre.Z.Subtract(reach).Lower > z.Add(expanded).Upper ||
            leaf.Centre.Z.Add(reach).Upper < z.Subtract(expanded).Lower ||
            leaf.Centre.Y.Subtract(reach).Lower > top || leaf.Centre.Y.Add(reach).Upper < bottom) return _work.Complete(GeometrySign.Negative, ref member);
        if (!Begin(leaf, request.Axis.X, request.Axis.Y)) return _work.Complete(_tilted.Measure(owner, leaf, part, request, out member), ref member);
        GeometrySign admitted = Admission(leaf, part, Add(C(request.Radius), C(request.ContactBand)), C(bottom), C(top));
        if (admitted != GeometrySign.Positive) return _work.Complete(admitted, ref member);
        GeometrySign axis = Maximum(leaf, part, request.Axis.X, request.Axis.Y, C(0), out Witness axisPoint);
        GeometrySign footprint = Maximum(leaf, part, request.Axis.X, request.Axis.Y, C(request.Radius), out Witness point);
        if (axis == GeometrySign.Unresolved || footprint == GeometrySign.Unresolved) return _work.Complete(GeometrySign.Unresolved, ref member);
        byte count = 0;
        SupportFootprintWitness first = default, second = default;
        if (axis == GeometrySign.Positive)
        {
            if (!Publish(axisPoint, SupportWitnessRole.Axis, request.CosMaxSlope, out first)) return _work.Complete(GeometrySign.Unresolved, ref member);
            count++;
        }
        if (footprint == GeometrySign.Positive)
        {
            if (!Publish(point, SupportWitnessRole.Footprint, request.CosMaxSlope, out var witness)) return _work.Complete(GeometrySign.Unresolved, ref member);
            if (count == 0) first = witness; else second = witness;
            count++;
        }
        GeometryInterval height = Bounds(point.Position.Y);
        member = new(owner, SupportElementKind.Tangent, leaf.LeafId * SupportNeighborhoodPolyhedra.FaceStride + part,
            default, 0, first.Normal, first.NormalError, first.UpwardLower, footprint == GeometrySign.Positive,
            footprint == GeometrySign.Positive ? height.Lower : 0, footprint == GeometrySign.Positive ? height.Upper : 0,
            0, count, first, second);
        return _work.Complete(GeometrySign.Positive, ref member);
    }

    bool Publish(Witness point, SupportWitnessRole role, double cosine, out SupportFootprintWitness witness)
    {
        GeometryVector normal = point.Normal.Bounds(_arithmetic);
        if (normal.IsResolved && normal.Y.Lower < cosine && normal.Y.Upper >= cosine &&
            Sign(Sub(point.Normal.Y, C(cosine))) == GeometrySign.Zero)
            normal = new(normal.X, GeometryInterval.Exact(cosine), normal.Z);
        return _work.Complete(SupportFootprintPublication.TryWitness(role, point.Position.Bounds(_arithmetic),
            normal, out witness), ref witness);
    }

    GeometrySign Maximum(in SupportCurvedLeaf leaf, int part, double x, double z, Number disc, out Witness witness)
    {
        witness = default;
        if (leaf.Shape != SupportCurvedShape.Cylinder) return _work.Complete(RoundMaximum(x, z, disc, out witness), ref witness);
        if (_upright) return _work.Complete(UprightMaximum(part, x, z, disc, out witness), ref witness);
        return _work.Complete(HorizontalMaximum(part, disc, out witness), ref witness);
    }

    Number CoreParameter()
    {
        // Cauchy bounds every unit-axis projection by the query's distance from the core centre.
        // A closed bounding-circle endpoint remains in the strip for every enclosed unit axis.
        GeometrySign circle = Sign(Sub(_distanceSquared, Sq(_half)));
        return circle is GeometrySign.Negative or GeometrySign.Zero ? _p : _arithmetic.Clamp(_p, _half);
    }

    GeometrySign RoundMaximum(double x, double z, Number disc, out Witness witness)
    {
        witness = default;
        Point core = _centre;
        Number dx = Sub(C(x), core.X), dz = Sub(C(z), core.Z);
        Number distanceSquared = _distanceSquared;
        if (!_sphere && _upright) core = core with { Y = Add(core.Y, _half) };
        else if (!_sphere)
        {
            Number t = CoreParameter();
            core = Add(core, Scale(_e, t));
            Number gap = Sub(_p, t);
            Point offset = Add(Scale(_e, gap), Scale(_f, _q));
            dx = offset.X; dz = offset.Z;
            distanceSquared = Add(Sq(_q), Sq(gap));
        }
        bool axis = Sign(disc) == GeometrySign.Zero;
        Number distance = Root(distanceSquared), nearest = axis ? distance : Max(Sub(distance, disc), C(0));
        GeometrySign exists = Sign(Sub(_radius, nearest));
        if (exists == GeometrySign.Negative) return _work.Complete(exists, ref witness);
        if (exists == GeometrySign.Unresolved) return _work.Complete(exists, ref witness);
        Number dy = Root(Sub(Sq(_radius), Sq(nearest)));
        Number rx, rz;
        if (axis)
        {
            // The axis keeps its signed source offset even when the core-distance box contains zero.
            rx = dx; rz = dz;
        }
        else if (Sign(nearest) == GeometrySign.Zero)
            rx = rz = C(0);
        else
        {
            // Inside the disc the numerator is zero. Its positive radius bounds the divisor at the seam.
            Number factor = Div(nearest, Max(distance, disc));
            rx = Mul(dx, factor); rz = Mul(dz, factor);
        }
        var radial = new Point(rx, dy, rz);
        witness = new(Add(core, radial), Scale(radial, Div(C(1), _radius)));
        return _work.Complete(witness.Position.Bounds(_arithmetic).IsResolved && witness.Normal.Bounds(_arithmetic).IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved, ref witness);
    }

    GeometrySign UprightMaximum(int part, double x, double z, Number disc, out Witness witness)
    {
        witness = default;
        Number distance = Root(_distanceSquared);
        bool side = part == SupportNeighborhoodCurved.Side;
        int sign = part == SupportNeighborhoodCurved.TopCap ? _axisSign : -_axisSign;
        if (!side && sign < 0) return _work.Complete(GeometrySign.Negative, ref witness);
        GeometrySign near = Sign(Sub(Add(distance, disc), _radius));
        GeometrySign far = Sign(Sub(distance, Add(_radius, disc)));
        if ((side && near == GeometrySign.Negative) || far == GeometrySign.Positive) return _work.Complete(GeometrySign.Negative, ref witness);
        if ((side && near == GeometrySign.Unresolved) || far == GeometrySign.Unresolved) return _work.Complete(GeometrySign.Unresolved, ref witness);
        Number factor = Sign(distance) == GeometrySign.Zero ? C(0) :
            side ? Div(_radius, distance) : Min(C(1), Div(_radius, distance));
        Number dx = Sub(C(x), _centre.X), dz = Sub(C(z), _centre.Z);
        Point position = new(Add(_centre.X, Mul(dx, factor)), Add(_centre.Y, _half), Add(_centre.Z, Mul(dz, factor)));
        Point normal = Up;
        if (side)
        {
            normal = Sign(distance) == GeometrySign.Zero ? new(C(1), C(0), C(0)) :
                new(Div(dx, distance), C(0), Div(dz, distance));
            if (Sign(distance) == GeometrySign.Zero) position = position with { X = Add(_centre.X, _radius) };
        }
        witness = new(position, normal);
        return _work.Complete(position.Bounds(_arithmetic).IsResolved && normal.Bounds(_arithmetic).IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved, ref witness);
    }

    GeometrySign HorizontalMaximum(int part, Number disc, out Witness witness)
    {
        witness = default;
        bool side = part == SupportNeighborhoodCurved.Side;
        Number t = side ? CoreParameter() : Mul(C(part == SupportNeighborhoodCurved.TopCap ? 1 : -1), _half);
        Number gap = Sub(_p, t), square = Sub(Sq(disc), Sq(gap));
        GeometrySign feasible = Sign(square);
        if (feasible == GeometrySign.Negative || feasible == GeometrySign.Unresolved) return _work.Complete(feasible, ref witness);
        Number width = Root(square), lateral = Sub(_q, _arithmetic.Clamp(_q, width));
        GeometrySign inside = Sign(Sub(_radius, Abs(lateral)));
        if (inside == GeometrySign.Negative || inside == GeometrySign.Unresolved) return _work.Complete(inside, ref witness);
        // At zero extent the attained coordinate is q itself, including a sign-uncertain seam.
        if (Sign(square) == GeometrySign.Zero) lateral = _q;
        Number height = Root(Sub(Sq(_radius), Sq(lateral)));
        Point radial = Add(Scale(_f, lateral), Scale(Up, height));
        Point position = Add(Add(_centre, Scale(_e, t)), radial);
        Point normal = side ? Scale(radial, Div(C(1), _radius)) :
            Scale(_e, C(part == SupportNeighborhoodCurved.TopCap ? 1 : -1));
        witness = new(position, normal);
        return _work.Complete(position.Bounds(_arithmetic).IsResolved && normal.Bounds(_arithmetic).IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved, ref witness);
    }
}
