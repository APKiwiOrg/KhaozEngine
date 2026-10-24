using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

internal enum ConvexInitialState { Separated, Touching, Overlapping }

/// <summary>A scratch-bound original-source relation. A strict common point proves overlap,
/// not a penetration normal. Touching retains the complete closed support predicates at its
/// attained point, so incident cone directions can be checked without averaging seams.</summary>
internal readonly struct ConvexInitialEvidence
{
    internal ConvexInitialState State { get; }
    internal ConvexDistanceCertificate Pair { get; }
    internal bool IsAlive => Pair.IsAlive;
    internal bool HasCommonPoint { get; }
    readonly P _common;
    // Circular witnesses remain in Pair's retained original-root expression, never rounded to P.
    internal P CommonPoint => Pair.IsCircular ? throw new ConvexExactRefusalException() : _common;
    readonly P _direction;
    internal P Direction => Pair.IsCircular ? throw new ConvexExactRefusalException() : _direction;
    internal Q DirectionSquared => Pair.IsCircular
        ? Q.From(Pair.Request.Shape.Margin) * Q.From(Pair.Request.Shape.Margin) : P.Dot(_direction, _direction);
    internal GeometryVector UnitNormalBounds { get; }
    internal ConvexLeafEvidence Contact { get; }

    internal ConvexInitialEvidence(ConvexInitialState state, ConvexDistanceCertificate pair,
        bool common = false, P point = default, P direction = default,
        GeometryVector normal = default, ConvexLeafEvidence contact = default)
    {
        State = state; Pair = pair; HasCommonPoint = common; _common = point;
        _direction = direction; UnitNormalBounds = normal; Contact = contact;
    }
}
