using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Upload-time source magnitudes for posed LBS history boxes. Does not participate in culling,
/// source admission or deformation. The frame calculation is O(joints) with no managed allocation.</summary>
internal readonly struct LinearBlendMotionBounds
{
    // u = 2^-24. (1+u)^8 < 1+16u bounds four products and four additions.
    // The propagated error of eight flushed results is below 16 eta. Contraction only reduces error.
    const double RoundFactor = 1 + 16.0 / (1 << 24);
    const double Eta = 1.17549435082228750796873653722224568e-38;
    readonly Vector3 _positionMagnitude;
    readonly double _weightMagnitude;
    readonly double _minimumTotal;
    readonly bool _finite;
    readonly bool _empty;

    public LinearBlendMotionBounds(ReadOnlySpan<SkinnedVertex> vertices)
    {
        _empty = vertices.IsEmpty;
        _finite = true;
        _positionMagnitude = Vector3.Zero;
        // LBS returns the identity when the rounded total is below 1e-8, including negative totals.
        _weightMagnitude = 1;
        _minimumTotal = 1;
        foreach (ref readonly SkinnedVertex vertex in vertices)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float position = vertex.Position[axis];
                if (!float.IsFinite(position)) _finite = false;
            }
            _positionMagnitude = Vector3.Max(_positionMagnitude, Vector3.Abs(vertex.Position));
            double weight = 0, lowerTotal = 0;
            for (int slot = 0; slot < 4; slot++)
            {
                float component = vertex.BoneWeights[slot];
                if (!float.IsFinite(component)) _finite = false;
                weight = AddUp(weight, Math.Abs((double)component));
                lowerTotal = Math.BitDecrement(lowerTotal + component);
            }
            _weightMagnitude = Math.Max(_weightMagnitude, weight);
            Vector4 weights = vertex.BoneWeights;
            float total = ((weights.X + weights.Y) + weights.Z) + weights.W;
            if (!float.IsFinite(total)) _finite = false;
            if (total >= 1e-8f)
            {
                // The GPU preserves homogeneous W through placement. Bound its rounded weight sum
                // below before dividing to Cartesian coordinates. Identity fallback has W exactly one.
                double error = AddUp(MulUp(RoundFactor - 1, weight), 16 * Eta);
                double lower = Math.BitDecrement(lowerTotal - error);
                if (lower < 1e-8) _finite = false;
                _minimumTotal = Math.Min(_minimumTotal, lower);
            }
        }
    }

    public MeshBounds BoundWorld(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 world)
    {
        if (_empty) return MeshBounds.Empty;
        if (!_finite) return Unbounded;

        // LBS does not enforce nonnegative or normalized weights. For every blended coefficient,
        // |sum(w_i * M_i)| <= max_vertex(sum(abs(w_i))) * max_joint(abs(M_joint)).
        // Include identity coefficients for the low-total fallback. Only XYZ columns affect positions.
        Span<double> coefficients = stackalloc double[12];
        coefficients.Clear();
        coefficients[0] = coefficients[4] = coefficients[8] = 1;
        foreach (ref readonly Matrix4x4 bone in palette)
        {
            // An arbitrary projective LBS joint can cross W=0 inside the source. Without a proved
            // positive denominator it needs an unbounded box, while submission remains accepted.
            if (bone.M14 != 0 || bone.M24 != 0 || bone.M34 != 0 || bone.M44 != 1) return Unbounded;
            for (int row = 0; row < 4; row++)
                for (int axis = 0; axis < 3; axis++)
                {
                    float value = bone[row, axis];
                    if (!float.IsFinite(value)) return Unbounded;
                    int index = row * 3 + axis;
                    coefficients[index] = Math.Max(coefficients[index], Math.Abs((double)value));
                }
        }
        for (int i = 0; i < coefficients.Length; i++)
        {
            coefficients[i] = RoundMagnitude(MulUp(_weightMagnitude, coefficients[i]));
            // A possible intermediate binary32 overflow cannot be certified by a finite box.
            if (coefficients[i] > float.MaxValue) return Unbounded;
        }

        Vector3 extent = default;
        Span<double> rawExtent = stackalloc double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            double magnitude = coefficients[9 + axis];
            for (int row = 0; row < 3; row++)
                magnitude = AddUp(magnitude, MulUp(_positionMagnitude[row], coefficients[row * 3 + axis]));
            magnitude = RoundMagnitude(magnitude);
            if (magnitude > float.MaxValue) return Unbounded;
            rawExtent[axis] = magnitude;
            // MinimumTotal <= 1 encloses both CPU XYZ and the GPU's homogeneous quotient.
            magnitude = Math.BitIncrement(magnitude / _minimumTotal);
            if (magnitude > float.MaxValue) return Unbounded;
            float rounded = (float)magnitude;
            if (rounded < magnitude) rounded = MathF.BitIncrement(rounded);
            extent[axis] = rounded;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            // Check GPU world arithmetic before homogeneous division, including translation * W.
            double magnitude = MulUp(Math.Abs((double)world[3, axis]), RoundMagnitude(_weightMagnitude));
            for (int row = 0; row < 3; row++)
                magnitude = AddUp(magnitude, MulUp(rawExtent[row], Math.Abs((double)world[row, axis])));
            if (!double.IsFinite(magnitude) || RoundMagnitude(magnitude) > float.MaxValue) return Unbounded;
        }

        // Reuse the outward affine placement enclosure, including its binary32 placement error.
        // Its 64u relative allowance covers the GPU numerator after division. MinimumTotal >= 1e-8
        // keeps the flushed world error below 16 eta / 1e-8, well inside its 2^-60 minimum padding.
        // LBS historically accepts nonfinite, overflowing and non-affine inputs. Preserve acceptance
        // and retain an unbounded history box when a finite enclosure cannot be represented.
        var modelBounds = new MeshBounds(-extent, extent);
        return DualQuaternionBounds.TryBoundWorld(modelBounds, world, out MeshBounds bounds) ? bounds : Unbounded;
    }

    static MeshBounds Unbounded => new(new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity));
    static double RoundMagnitude(double value) => AddUp(MulUp(RoundFactor, value), 16 * Eta);
    static double AddUp(double a, double b) => a == 0 ? b : b == 0 ? a : Math.BitIncrement(a + b);
    static double MulUp(double a, double b) => a == 0 || b == 0 ? 0 : Math.BitIncrement(a * b);
}
