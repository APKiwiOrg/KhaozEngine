using System;
using System.Collections.Generic;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal enum SupportCurvedShape : byte { Sphere, Capsule, Cylinder }

/// <summary>A curved leaf as the rigid solid the neighborhood measures: the leaf's installed world centre, the unit
/// direction of its installed local Y axis, its radius and its half length. A sphere is round about its centre, a
/// capsule is round about its core segment, and a cylinder has a side and two caps. Published errors enclose this
/// rigid solid, with the centre and Y column of the leaf's float world pose, not the float path Bepu evaluates for
/// each colliding pair, which can differ from it by about 1e-7 m. The rows of I - Axis Axis^T
/// project an offset across the axis with each offset component used once, so interval dependency does not widen
/// the radial direction along the axis.</summary>
internal readonly record struct SupportCurvedLeaf(int LeafId, SupportCurvedShape Shape, GeometryVector Centre,
    GeometryVector Axis, double Radius, double HalfLength, GeometryVector AcrossX, GeometryVector AcrossY,
    GeometryVector AcrossZ)
{
    internal int Parts => Shape == SupportCurvedShape.Cylinder ? 3 : 1;

    internal GeometryVector Across(GeometryVector offset) => new(GeometryVectorOperations.Dot(AcrossX, offset),
        GeometryVectorOperations.Dot(AcrossY, offset), GeometryVectorOperations.Dot(AcrossZ, offset));
}

/// <summary>Sphere, capsule and cylinder leaves of one installed static, under the same installed pose and compound
/// composition proofs as polyhedron leaves. Box and hull leaves have their own owner, and a polyhedron leaf never
/// refuses the curved leaves beside it. A cylinder's base alignment is its compound leaf pose, so the composed
/// world pose already carries the lift.</summary>
internal static class SupportNeighborhoodCurved
{
    internal const int Side = 0, TopCap = 1, BottomCap = 2;

    internal static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex shape, in RigidPose pose,
        List<SupportCurvedLeaf> leaves)
    {
        leaves.Clear();
        bool compound = shape.Type == default(Compound).TypeId;
        if (!compound && !Curved(shape)) return CapsuleFeatureStatus.Complete;
        // Leaf proves this same pose first and refuses it as Unsupported.
        if (!compound) return Leaf(simulation, shape, pose, 0, leaves);
        if (!CapsuleFeatureGeometry.ProveInstalledPose(pose)) return CapsuleFeatureStatus.Unsupported;
        ref Compound installed = ref simulation.Shapes.GetShape<Compound>(shape.Index);
        if (installed.Children.Length is 0 or > CapsuleFeatureGeometry.MaximumLeaves)
            return CapsuleFeatureStatus.Unsupported;
        for (int i = 0; i < installed.Children.Length; i++)
        {
            ref var child = ref installed.Children[i];
            if (!Curved(child.ShapeIndex)) continue;
            if (!CapsuleFeatureGeometry.ProveInstalledPose(child.LocalPose)) return CapsuleFeatureStatus.Unsupported;
            // Exactly the pinned backend's composition, including its represented rounding.
            Compound.GetWorldPose(child.LocalPose, pose, out RigidPose worldPose);
            if (!InstalledPoseOperations.ProveComposition(child.LocalPose, pose, worldPose))
                return CapsuleFeatureStatus.Unsupported;
            CapsuleFeatureStatus status = Leaf(simulation, child.ShapeIndex, worldPose, i, leaves);
            if (status != CapsuleFeatureStatus.Complete) return status;
        }
        return CapsuleFeatureStatus.Complete;
    }

    // The axis is the normalized enclosure of the installed operator's Y column, which holds both the real
    // quaternion polynomial and the represented maps. Sizes share the local bound of captured polyhedron vertices.
    static CapsuleFeatureStatus Leaf(Simulation simulation, TypedIndex index, in RigidPose pose, int leafId,
        List<SupportCurvedLeaf> leaves)
    {
        if (!InstalledPoseOperator.TryCreate(pose, out InstalledPoseOperator transform))
            return CapsuleFeatureStatus.Unsupported;
        SupportCurvedShape shape;
        float radius, half;
        if (index.Type == default(Sphere).TypeId)
        {
            shape = SupportCurvedShape.Sphere;
            radius = simulation.Shapes.GetShape<Sphere>(index.Index).Radius;
            half = 0;
        }
        else if (index.Type == default(Capsule).TypeId)
        {
            ref Capsule capsule = ref simulation.Shapes.GetShape<Capsule>(index.Index);
            shape = SupportCurvedShape.Capsule;
            (radius, half) = (capsule.Radius, capsule.HalfLength);
        }
        else
        {
            ref Cylinder cylinder = ref simulation.Shapes.GetShape<Cylinder>(index.Index);
            shape = SupportCurvedShape.Cylinder;
            (radius, half) = (cylinder.Radius, cylinder.HalfLength);
        }
        const float local = RepresentedGeometryTransforms.MaximumLocalMagnitude;
        if (!(radius > 0 && radius <= local) || !(half >= 0 && half <= local) ||
            (shape == SupportCurvedShape.Cylinder && half == 0))
            return CapsuleFeatureStatus.Unsupported;
        GeometryVector axis = GeometryVectorOperations.Normalize(transform.Y.Bounds);
        if (!axis.IsResolved) return CapsuleFeatureStatus.Unsupported;
        GeometryInterval one = GeometryInterval.Exact(1);
        GeometryInterval xy = Negate(axis.X.Multiply(axis.Y)), xz = Negate(axis.X.Multiply(axis.Z));
        GeometryInterval yz = Negate(axis.Y.Multiply(axis.Z));
        var acrossX = new GeometryVector(one.Subtract(axis.X.Square()), xy, xz);
        var acrossY = new GeometryVector(xy, one.Subtract(axis.Y.Square()), yz);
        var acrossZ = new GeometryVector(xz, yz, one.Subtract(axis.Z.Square()));
        if (!acrossX.IsResolved || !acrossY.IsResolved || !acrossZ.IsResolved) return CapsuleFeatureStatus.Unsupported;
        leaves.Add(new(leafId, shape, FeaturePoint.Exact(pose.Position).Bounds, axis, radius, half, acrossX, acrossY,
            acrossZ));
        return CapsuleFeatureStatus.Complete;
    }

    static GeometryInterval Negate(GeometryInterval value) =>
        !value.IsResolved ? default : GeometryInterval.Enclose(-value.Upper, -value.Lower);

    static bool Curved(TypedIndex shape) => shape.Type == default(Sphere).TypeId ||
        shape.Type == default(Capsule).TypeId || shape.Type == default(Cylinder).TypeId;
}

/// <summary>Measures tangent elements against one upright probe axis. Each axis point has a closed form closest
/// point on a sphere, a capsule, a cylinder side or a cylinder cap, and the minimum over the axis is bounded by
/// bisecting the axis's height range: a span whose distance lower bound exceeds a distance some axis point certainly
/// reaches cannot hold the minimum and is dropped. The minimum lies in a kept span, so the kept spans' lower bounds
/// bound it from below and their closest points, normals and heights enclose its witness, normal and side. Where the
/// arithmetic widens, near tangency or on a flat stretch of axis, the kept spans widen the published errors instead
/// of refusing. A kept span whose normal is unbounded means the element has no certifiable normal there, and it is
/// not a member.</summary>
internal sealed class SupportTangentKernel
{
    const int MaximumSpans = 16;
    const int MaximumDepth = 64;
    // Spans this short along the axis are not split further.
    const double Resolution = 1.0 / (1 << 22);

    readonly record struct Sample(GeometryInterval Distance, GeometryInterval Height, GeometryVector Witness,
        GeometryVector Normal);

    readonly record struct Span(double Low, double High, Sample Sample);

    GeometryInterval _x, _z, _radius;
    double _low, _high, _certainLow, _certainHigh, _band;
    List<Span> _spans = [], _next = [];

    /// <summary>Aims the reused kernel at the upright axis from <paramref name="start"/> to <paramref name="end"/>.
    /// Its height range is enclosed by the outer bounds of both ends, and every height between their inner bounds is
    /// certainly on it.</summary>
    internal void Reset(FeaturePoint start, FeaturePoint end, GeometryInterval radius, double band)
    {
        _x = start.X.Bounds;
        _z = start.Z.Bounds;
        _low = start.Y.Bounds.Lower;
        _high = end.Y.Bounds.Upper;
        _certainLow = start.Y.Bounds.Upper;
        _certainHigh = end.Y.Bounds.Lower;
        _radius = radius;
        _band = band;
    }

    /// <summary>Positive with the published element when it is a member, Negative when it is not, and Unresolved
    /// when the bounded arithmetic failed.</summary>
    internal GeometrySign Measure(StaticHandle owner, in SupportCurvedLeaf leaf, int part, out SupportElement element)
    {
        element = default;
        double upper = double.PositiveInfinity;
        if (_certainLow <= _certainHigh &&
            (!Reach(leaf, part, _certainLow, ref upper) || !Reach(leaf, part, _certainHigh, ref upper)))
            return GeometrySign.Unresolved;
        Sample root = Evaluate(leaf, part, GeometryInterval.Enclose(_low, _high));
        if (!root.Distance.IsResolved) return GeometrySign.Unresolved;
        // Every axis point lies in the root span, so its upper bound is reached.
        upper = Math.Min(upper, root.Distance.Upper);
        _spans.Clear();
        _spans.Add(new(_low, _high, root));
        for (int depth = 0; ; depth++)
        {
            Prune(upper);
            if (_spans.Count == 0) return GeometrySign.Unresolved;
            if (GeometryInterval.Exact(Lowest()).Subtract(_radius).Lower > _band) return GeometrySign.Negative;
            if (depth == MaximumDepth || _spans.Count * 2 > MaximumSpans || Narrow()) break;
            _next.Clear();
            foreach (Span span in _spans)
            {
                double middle = span.Low * 0.5 + span.High * 0.5;
                if (!(middle > span.Low && middle < span.High))
                {
                    _next.Add(span);
                    continue;
                }
                Sample first = Evaluate(leaf, part, GeometryInterval.Enclose(span.Low, middle));
                Sample second = Evaluate(leaf, part, GeometryInterval.Enclose(middle, span.High));
                if (!first.Distance.IsResolved || !second.Distance.IsResolved) return GeometrySign.Unresolved;
                _next.Add(new(span.Low, middle, first));
                _next.Add(new(middle, span.High, second));
                if (middle >= _certainLow && middle <= _certainHigh && !Reach(leaf, part, middle, ref upper))
                    return GeometrySign.Unresolved;
            }
            (_spans, _next) = (_next, _spans);
        }
        GeometryInterval separation = GeometryInterval.Enclose(Lowest(), upper).Subtract(_radius);
        if (!separation.IsResolved) return GeometrySign.Unresolved;
        if (separation.Lower > _band) return GeometrySign.Negative;
        bool front = false;
        GeometryVector normal = default, witness = default;
        for (int i = 0; i < _spans.Count; i++)
        {
            Sample sample = _spans[i].Sample;
            if (!sample.Normal.IsResolved || !sample.Witness.IsResolved) return GeometrySign.Negative;
            // An unresolved height never excludes a member.
            front |= !sample.Height.IsResolved || sample.Height.Upper >= -_band;
            normal = i == 0 ? sample.Normal : Hull(normal, sample.Normal);
            witness = i == 0 ? sample.Witness : Hull(witness, sample.Witness);
        }
        if (!front) return GeometrySign.Negative;
        GeometryVectorOutput publishedNormal = GeometryVectorOperations.Publish(normal);
        GeometryVectorOutput publishedWitness = GeometryVectorOperations.Publish(witness);
        if (!publishedNormal.IsResolved || !publishedWitness.IsResolved) return GeometrySign.Unresolved;
        element = new SupportElement(owner, SupportElementKind.Tangent,
            leaf.LeafId * SupportNeighborhoodPolyhedra.FaceStride + part, publishedNormal.Value, publishedNormal.Error,
            publishedWitness.Value, publishedWitness.Error, separation.Lower, separation.Upper);
        return GeometrySign.Positive;
    }

    // A height certainly on the axis: its distance bound is reached.
    bool Reach(in SupportCurvedLeaf leaf, int part, double height, ref double upper)
    {
        Sample sample = Evaluate(leaf, part, GeometryInterval.Exact(height));
        if (!sample.Distance.IsResolved) return false;
        upper = Math.Min(upper, sample.Distance.Upper);
        return true;
    }

    void Prune(double upper)
    {
        int kept = 0;
        for (int i = 0; i < _spans.Count; i++)
            if (_spans[i].Sample.Distance.Lower <= upper) _spans[kept++] = _spans[i];
        _spans.RemoveRange(kept, _spans.Count - kept);
    }

    double Lowest()
    {
        double lowest = double.PositiveInfinity;
        foreach (Span span in _spans) lowest = Math.Min(lowest, span.Sample.Distance.Lower);
        return lowest;
    }

    bool Narrow()
    {
        foreach (Span span in _spans)
            if (span.High - span.Low > Resolution) return false;
        return true;
    }

    /// <summary>The element's closest point to every axis point at a height in <paramref name="height"/>: the
    /// distance, the axis point's signed height above the tangent plane there, the closest point and its normal.</summary>
    Sample Evaluate(in SupportCurvedLeaf leaf, int part, GeometryInterval height)
    {
        var point = new GeometryVector(_x, height, _z);
        GeometryVector offset = Subtract(point, leaf.Centre);
        if (leaf.Shape == SupportCurvedShape.Sphere) return Round(leaf.Centre, offset, leaf.Radius);
        GeometryInterval along = GeometryVectorOperations.Dot(offset, leaf.Axis);
        GeometryVector across = leaf.Across(offset);
        if (leaf.Shape == SupportCurvedShape.Capsule)
        {
            // Beside the core the offset from it is the offset across the axis. Beyond an end it is the offset from
            // that end. A span reaching both encloses both.
            GeometryInterval core = Clamp(along, leaf.HalfLength);
            // Each end is chosen by which side of the core it bounds, never by its sign, so a zero length capsule
            // whose ends are both zero still encloses every axis point.
            GeometryVector radial = default;
            if (along.Upper > -leaf.HalfLength && along.Lower < leaf.HalfLength) radial = across;
            if (along.Lower <= -leaf.HalfLength) radial = Join(radial, Beyond(offset, leaf.Axis, -leaf.HalfLength));
            if (along.Upper >= leaf.HalfLength) radial = Join(radial, Beyond(offset, leaf.Axis, leaf.HalfLength));
            return Round(Add(leaf.Centre, Scale(leaf.Axis, core)), radial, leaf.Radius);
        }
        GeometryInterval distance = SupportGeometry.Length(across);
        GeometryInterval radius = GeometryInterval.Exact(leaf.Radius), half = GeometryInterval.Exact(leaf.HalfLength);
        if (part == SupportNeighborhoodCurved.Side)
        {
            // Radial from the axis, at the axis point's height clamped to the side.
            GeometryVector outward = Divide(across, distance);
            GeometryInterval above = distance.Subtract(radius);
            GeometryInterval beyond = Positive(InstalledPoseOperations.Abs(along).Subtract(half));
            GeometryVector level = Add(leaf.Centre, Scale(leaf.Axis, Clamp(along, leaf.HalfLength)));
            return new(Hypot(above, beyond), above, Add(level, Scale(outward, radius)), outward);
        }
        // A cap is the disc at one end, normal along the axis. Inside the rim the closest point is straight down
        // the axis, and outside it is on the rim.
        GeometryInterval sign = GeometryInterval.Exact(part == SupportNeighborhoodCurved.TopCap ? 1 : -1);
        GeometryVector normal = Scale(leaf.Axis, sign);
        GeometryInterval rise = along.Multiply(sign).Subtract(half);
        GeometryInterval outside = Positive(distance.Subtract(radius));
        GeometryVector centre = Add(leaf.Centre, Scale(normal, half));
        return new(Hypot(rise, outside), rise, Add(centre, Scale(across, Shrink(distance, leaf.Radius))), normal);
    }

    // The offset of an axis point from the core end at the signed half length.
    static GeometryVector Beyond(GeometryVector offset, GeometryVector axis, double end) =>
        Subtract(offset, Scale(axis, GeometryInterval.Exact(end)));

    static GeometryVector Join(GeometryVector enclosure, GeometryVector more) =>
        enclosure.IsResolved ? Hull(enclosure, more) : more;

    // Round about a core point: the closest surface point is radial from the core.
    static Sample Round(GeometryVector core, GeometryVector offset, double radius)
    {
        GeometryInterval length = SupportGeometry.Length(offset);
        GeometryInterval above = length.Subtract(GeometryInterval.Exact(radius));
        GeometryVector normal = Divide(offset, length);
        return new(InstalledPoseOperations.Abs(above), above, Add(core, Scale(normal, GeometryInterval.Exact(radius))),
            normal);
    }

    // min(1, radius / distance), the factor that brings a point outside the rim onto it.
    static GeometryInterval Shrink(GeometryInterval distance, double radius)
    {
        if (!distance.IsResolved) return default;
        GeometryInterval r = GeometryInterval.Exact(radius);
        double lower = distance.Upper <= radius ? 1 : r.Divide(GeometryInterval.Exact(distance.Upper)).Lower;
        double upper = distance.Lower <= radius ? 1 : r.Divide(GeometryInterval.Exact(distance.Lower)).Upper;
        return GeometryInterval.Enclose(Math.Min(1, lower), Math.Min(1, upper));
    }

    static GeometryInterval Clamp(GeometryInterval value, double half) => !value.IsResolved ? default
        : GeometryInterval.Enclose(Math.Clamp(value.Lower, -half, half), Math.Clamp(value.Upper, -half, half));

    static GeometryInterval Positive(GeometryInterval value) => !value.IsResolved ? default
        : GeometryInterval.Enclose(Math.Max(0, value.Lower), Math.Max(0, value.Upper));

    static GeometryInterval Hypot(GeometryInterval a, GeometryInterval b) =>
        SupportGeometry.Length(new GeometryVector(a, b, GeometryInterval.Exact(0)));

    static GeometryVector Add(GeometryVector a, GeometryVector b) => new(a.X.Add(b.X), a.Y.Add(b.Y), a.Z.Add(b.Z));

    static GeometryVector Subtract(GeometryVector a, GeometryVector b) =>
        new(a.X.Subtract(b.X), a.Y.Subtract(b.Y), a.Z.Subtract(b.Z));

    static GeometryVector Scale(GeometryVector a, GeometryInterval s) =>
        new(a.X.Multiply(s), a.Y.Multiply(s), a.Z.Multiply(s));

    // Unresolved when the divisor may be zero, which is where a radial normal has no bound.
    static GeometryVector Divide(GeometryVector a, GeometryInterval s) =>
        new(a.X.Divide(s), a.Y.Divide(s), a.Z.Divide(s));

    static GeometryVector Hull(GeometryVector a, GeometryVector b) => new(Hull(a.X, b.X), Hull(a.Y, b.Y), Hull(a.Z, b.Z));

    static GeometryInterval Hull(GeometryInterval a, GeometryInterval b) =>
        GeometryInterval.Enclose(Math.Min(a.Lower, b.Lower), Math.Max(a.Upper, b.Upper));
}
