using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Immutable radius metadata for one validated version 1 rigid DQ source. Construct once at
    /// source admission and recreate if any source words change. No vertex storage is retained.</summary>
    public readonly struct DqBoundsSource
    {
        public double Radius { get; }
        public int BoneCount { get; }
        internal bool Validated { get; }
        internal bool Empty { get; }

        public DqBoundsSource(ReadOnlySpan<SkinnedVertex> vertices, int boneCount)
        {
            SkinningValidation.ValidateSource(vertices, boneCount, SkinningProfile.DualQuaternionRigid,
                new SkinningValidationContext("DQ bounds source", null, 0));
            double radius = 0;
            foreach (ref readonly SkinnedVertex vertex in vertices)
                radius = Math.Max(radius, DualQuaternionBounds.SourceRadius(vertex.Position));
            Radius = radius;
            BoneCount = boneCount;
            Empty = vertices.IsEmpty;
            Validated = true;
        }
    }

    /// <summary>Outward ordinary DQ enclosures, not coverage/carry certificates. Failure refuses admission.
    /// The scalar graph, constants and inequalities are proved in DUAL-QUATERNION-BOUNDS-2026-10-22.md.</summary>
    public static class DualQuaternionBounds
    {
        const double U = 1.0 / (1 << 24);
        // An absolute allowance of one minimum normal also covers flushed arithmetic results.
        const double Eta = 1.17549435082228750796873653722224568e-38;
        const double Sigma = 1.0 / (1 << 18); // 64u, not the matrix Gram tolerance.
        const double NormFloor = 1.0 / 8;
        const double MinimumPadding = 1.0 / (1L << 60);

        /// <summary>O(joints), with no posed-source scan. Bounds both the AABB and its stored sphere.
        /// An invalid palette, unproved real norm, overflow or unrepresentable sphere returns false.</summary>
        public static bool TryBoundModel(in DqBoundsSource source, ReadOnlySpan<RigidDualQuaternion> palette,
            out MeshBounds bounds)
        {
            bounds = default;
            if (!source.Validated || palette.Length != source.BoneCount) return false;
            double maximumDual = 0;
            foreach (ref readonly RigidDualQuaternion bone in palette)
            {
                if (!Finite(bone.Real) || !Finite(bone.Dual)) return false;
                NormSquared(bone.Real, out double lower, out double upper);
                if (lower < 1 - Sigma || upper > 1 + Sigma) return false;
                NormSquared(bone.Dual, out _, out double dualSquared);
                maximumDual = Math.Max(maximumDual, SqrtUp(dualSquared));
            }
            if (source.Empty)
            {
                bounds = MeshBounds.Empty;
                return true;
            }

            // Four ordered contributions: ||Dsum|| <= (1+64u) max||d|| + 64 eta.
            double dual = maximumDual == 0 ? 0 : AddUp(MulUp(1 + Sigma, maximumDual), 64 * Eta);
            double radius = source.Radius;
            double rotationError = AddUp(MulUp(3 * Sigma, radius), 8 * Eta);
            double translationError = AddUp(MulUp(Sigma, dual), 32 * Eta);
            // Absolute sums, including all partial sums and the final doubling, must stay finite.
            if (!FloatMagnitude(AddUp(MulUp(9, radius), 8 * Eta)) ||
                !FloatMagnitude(AddUp(MulUp(4, dual), translationError))) return false;

            // Both the exact squared norm N and the graph's rounded n exceed 1/8.
            // |n-N| <= Sigma, so N/n <= 1+Sigma/(1/8). Retain reciprocal rounding separately.
            double ratio = AddUp(1, DivUp(Sigma, NormFloor));
            double reciprocal = DivUp(1 + U, NormFloor);
            double factor = MulUp(ratio, 1 + U);
            double rotated = RoundMagnitude(AddUp(MulUp(radius, factor), MulUp(rotationError, reciprocal)));
            double translation = DivUp(MulUp(2, dual), Down(Math.Sqrt(NormFloor)));
            double translated = RoundMagnitude(AddUp(MulUp(translation, factor), MulUp(translationError, reciprocal)));
            double extent = RoundMagnitude(AddUp(rotated, translated));
            return TryMakeBounds(-extent, -extent, -extent, extent, extent, extent, out bounds);
        }

        /// <summary>Encloses exact affine placement and binary32 affine evaluation, including shear,
        /// reflection and collapsed axes. Projective, nonfinite or overflowing placement refuses.</summary>
        public static bool TryBoundWorld(in MeshBounds modelBounds, in Matrix4x4 world, out MeshBounds worldBounds)
        {
            worldBounds = default;
            Vector3 min = modelBounds.Min, max = modelBounds.Max;
            if (!FiniteBox(min, max) || world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1)
                return false;
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    if (!float.IsFinite(world[row, column])) return false;

            Span<double> lo = stackalloc double[3], hi = stackalloc double[3], error = stackalloc double[3];
            for (int axis = 0; axis < 3; axis++)
            {
                lo[axis] = double.PositiveInfinity;
                hi[axis] = double.NegativeInfinity;
                double magnitude = Math.Abs((double)world[3, axis]);
                for (int component = 0; component < 3; component++)
                {
                    double position = Math.Max(Math.Abs((double)min[component]), Math.Abs((double)max[component]));
                    magnitude = AddUp(magnitude, MulUp(position, Math.Abs((double)world[component, axis])));
                }
                error[axis] = AddUp(MulUp(Sigma, magnitude), 8 * Eta);
                if (!FloatMagnitude(AddUp(magnitude, error[axis]))) return false;
            }
            for (int corner = 0; corner < 8; corner++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    double lower = 0, upper = 0;
                    for (int component = 0; component < 3; component++)
                    {
                        double p = (corner & (1 << component)) == 0 ? min[component] : max[component];
                        double coefficient = world[component, axis];
                        lower = Down(lower + Down(p * coefficient));
                        upper = Up(upper + Up(p * coefficient));
                    }
                    lower = Down(Down(lower + world[3, axis]) - error[axis]);
                    upper = Up(Up(upper + world[3, axis]) + error[axis]);
                    lo[axis] = Math.Min(lo[axis], lower);
                    hi[axis] = Math.Max(hi[axis], upper);
                }
            }
            return TryMakeBounds(lo[0], lo[1], lo[2], hi[0], hi[1], hi[2], out worldBounds);
        }

        internal static double SourceRadius(Vector3 p)
        {
            double sum = AddUp(MulUp(p.X, p.X), MulUp(p.Y, p.Y));
            return SqrtUp(AddUp(sum, MulUp(p.Z, p.Z)));
        }

        static void NormSquared(Vector4 value, out double lower, out double upper)
        {
            lower = upper = 0;
            for (int i = 0; i < 4; i++)
            {
                double component = value[i];
                lower = Math.Max(0, Down(lower + Math.Max(0, Down(component * component))));
                upper = AddUp(upper, MulUp(component, component));
            }
        }

        static bool TryMakeBounds(double lx, double ly, double lz, double hx, double hy, double hz, out MeshBounds bounds)
        {
            bounds = default;
            Span<double> lower = stackalloc double[] { lx, ly, lz }, upper = stackalloc double[] { hx, hy, hz };
            double magnitude = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!double.IsFinite(lower[i]) || !double.IsFinite(upper[i]) || lower[i] > upper[i]) return false;
                magnitude = Math.Max(magnitude, Math.Max(Math.Abs(lower[i]), Math.Abs(upper[i])));
            }
            // Padding is only a candidate construction. The sphere is certified below against the
            // original intervals, using the actual rounded Center and Radius from MeshBounds.
            double padding = Math.Max(MinimumPadding, MulUp(Sigma, magnitude));
            Vector3 min = default, max = default;
            for (int i = 0; i < 3; i++)
            {
                if (!ToFloat(Down(lower[i] - padding), false, out float low) ||
                    !ToFloat(Up(upper[i] + padding), true, out float high)) return false;
                min[i] = low;
                max[i] = high;
            }
            var candidate = new MeshBounds(min, max);
            if (!Finite(candidate.Center) || !float.IsFinite(candidate.Radius) || candidate.Radius < 0) return false;
            double squared = 0;
            for (int i = 0; i < 3; i++)
            {
                double distance = Math.Max(Up(candidate.Center[i] - lower[i]), Up(upper[i] - candidate.Center[i]));
                squared = AddUp(squared, MulUp(distance, distance));
            }
            double radius = candidate.Radius;
            if (squared > Down(radius * radius)) return false;
            bounds = candidate;
            return true;
        }

        static bool ToFloat(double value, bool upper, out float rounded)
        {
            rounded = (float)value;
            if (!float.IsFinite(rounded)) return false;
            if (upper && rounded < value) rounded = MathF.BitIncrement(rounded);
            if (!upper && rounded > value) rounded = MathF.BitDecrement(rounded);
            return float.IsFinite(rounded);
        }

        static double RoundMagnitude(double exact) => AddUp(MulUp(1 + U, exact), Eta);
        static double Up(double value) => Math.BitIncrement(value);
        static double Down(double value) => Math.BitDecrement(value);
        // Zero operations are exact, avoiding an artificial sqrt(epsilon) radius for a zero source.
        static double AddUp(double a, double b) => a == 0 ? b : b == 0 ? a : Up(a + b);
        static double MulUp(double a, double b) => a == 0 || b == 0 ? 0 : Up(a * b);
        static double DivUp(double a, double b) => a == 0 ? 0 : Up(a / b);
        static double SqrtUp(double value) => value == 0 ? 0 : Up(Math.Sqrt(value));
        static bool FloatMagnitude(double value) => double.IsFinite(value) && value >= 0 && value <= float.MaxValue;
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
        static bool FiniteBox(Vector3 min, Vector3 max) => Finite(min) && Finite(max) &&
            min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
    }
}
