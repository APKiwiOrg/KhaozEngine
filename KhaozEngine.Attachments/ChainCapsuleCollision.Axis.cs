using System;

namespace KhaozEngine.Attachments;

internal static partial class ChainCapsuleCollision
{
    // Capsule geometry is unchanged through all constraint and cull passes of the step.
    internal readonly struct Axis
    {
        private readonly Offset _start;
        private readonly Offset _end;
        private readonly Offset _axis;
        private readonly Offset _crossedEnds;
        private readonly double _inverseSquared;

        internal Axis(in ColliderCapsule capsule)
        {
            _start = new Offset(capsule.Start);
            _end = new Offset(capsule.End);
            _axis = _end - _start;
            double squared = _axis.LengthSquared;
            _inverseSquared = squared == 0 ? 0 : 1 / squared;
            _crossedEnds = Offset.Cross(_start, _end);
        }

        internal Offset Direction => _axis;
        internal bool IsPoint => _inverseSquared == 0;
        internal Offset Start => _start;
        internal Offset End => _end;

        internal Offset ClosestResidual(in ChainSegmentContact.Segment segment, out double fraction, out double along)
        {
            var root = segment.Root;
            var displacement = segment.Displacement;
            double segmentSquared = displacement.LengthSquared;
            fraction = 0;
            if (segmentSquared == 0)
                return Residual(root, default, out along);

            if (_inverseSquared == 0)
            {
                along = 0;
                return ResidualAtEnd(root - _start, displacement, segmentSquared, out fraction);
            }

            // Minimize against the infinite barrel first. Crossing before taking the dot products
            // avoids subtracting nearly equal squared lengths for near-parallel segments.
            var crossed = Offset.Cross(_axis, displacement);
            double crossedSquared = crossed.LengthSquared;
            fraction = crossedSquared == 0 ? 1 :
                Math.Clamp(-Offset.Dot(CrossedRoot(root), crossed) / crossedSquared, 0, 1);
            var residual = Residual(root, displacement * fraction, out along);
            if (along == 0)
                return ResidualAtEnd(root - _start, displacement, segmentSquared, out fraction);
            if (along == 1)
                return ResidualAtEnd(root - _end, displacement, segmentSquared, out fraction);
            return residual;
        }

        private static Offset ResidualAtEnd(Offset fromEnd, Offset displacement, double segmentSquared,
            out double fraction)
        {
            fraction = Math.Clamp(-Offset.Dot(fromEnd, displacement) / segmentSquared, 0, 1);
            return fromEnd + displacement * fraction;
        }

        // Match Residual's endpoint tests and operation order. A face intersection
        // only needs the axis fraction and can skip the barrel cross products.
        internal double Along(Offset root, Offset fromRoot)
        {
            if (_inverseSquared == 0)
                return 0;
            double tipProjection = Offset.Dot(fromRoot, _axis);
            double axisProjection = Offset.Dot(root - _start, _axis) + tipProjection;
            if (axisProjection <= 0)
                return 0;
            if (Offset.Dot(root - _end, _axis) + tipProjection >= 0)
                return 1;

            return axisProjection * _inverseSquared;
        }

        internal Offset Residual(Offset root, Offset tipFromRoot, out double along)
        {
            var rootFromStart = root - _start;
            var fromStart = rootFromStart + tipFromRoot;
            along = 0;
            if (_inverseSquared == 0)
                return fromStart;

            double tipProjection = Offset.Dot(tipFromRoot, _axis);
            double axisProjection = Offset.Dot(rootFromStart, _axis) + tipProjection;
            if (axisProjection <= 0)
                return fromStart;
            var rootFromEnd = root - _end;
            if (Offset.Dot(rootFromEnd, _axis) + tipProjection >= 0)
            {
                along = 1;
                return rootFromEnd + tipFromRoot;
            }

            along = axisProjection * _inverseSquared;

            // Expand the barrel's cross product to retain local offsets on very long axes.
            var crossed = CrossedRoot(root) + Offset.Cross(_axis, tipFromRoot);
            return Offset.Cross(crossed, _axis) * _inverseSquared;
        }

        private Offset CrossedRoot(Offset root) => Offset.Cross(_axis, root) + _crossedEnds;
    }
}
