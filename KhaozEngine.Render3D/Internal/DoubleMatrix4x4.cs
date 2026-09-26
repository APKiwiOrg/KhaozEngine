using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// A 4x4 matrix of doubles in the <see cref="Matrix4x4"/> row-vector convention, for products and inverses whose
    /// float rounding the result cannot afford. A <see cref="Matrix4x4"/> converts in exactly and <see cref="ToSingle"/>
    /// rounds each element once on the way out. Pure and allocation-free.
    /// </summary>
    internal readonly struct DoubleMatrix4x4
    {
        public readonly double M11, M12, M13, M14;
        public readonly double M21, M22, M23, M24;
        public readonly double M31, M32, M33, M34;
        public readonly double M41, M42, M43, M44;

        public DoubleMatrix4x4(double m11, double m12, double m13, double m14,
            double m21, double m22, double m23, double m24,
            double m31, double m32, double m33, double m34,
            double m41, double m42, double m43, double m44)
        {
            M11 = m11; M12 = m12; M13 = m13; M14 = m14;
            M21 = m21; M22 = m22; M23 = m23; M24 = m24;
            M31 = m31; M32 = m32; M33 = m33; M34 = m34;
            M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        }

        /// <summary>The same matrix, exactly.</summary>
        public DoubleMatrix4x4(in Matrix4x4 m)
            : this(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44)
        {
        }

        /// <summary>Each element rounded to the nearest float.</summary>
        public Matrix4x4 ToSingle() => new(
            (float)M11, (float)M12, (float)M13, (float)M14,
            (float)M21, (float)M22, (float)M23, (float)M24,
            (float)M31, (float)M32, (float)M33, (float)M34,
            (float)M41, (float)M42, (float)M43, (float)M44);

        /// <summary><paramref name="a"/> then <paramref name="b"/>, as <see cref="Matrix4x4"/> multiplies.</summary>
        public static DoubleMatrix4x4 operator *(in DoubleMatrix4x4 a, in DoubleMatrix4x4 b) => new(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
            a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
            a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
            a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
            a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
            a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
            a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
            a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
            a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
            a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
            a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
            a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
            a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
            a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

        /// <summary>The inverse by cofactors, as <see cref="Matrix4x4.Invert"/> computes it. False, with
        /// <paramref name="inverse"/> all NaN, when the determinant is zero or not finite.</summary>
        public static bool TryInvert(in DoubleMatrix4x4 m, out DoubleMatrix4x4 inverse)
        {
            double a = m.M11, b = m.M12, c = m.M13, d = m.M14;
            double e = m.M21, f = m.M22, g = m.M23, h = m.M24;
            double i = m.M31, j = m.M32, k = m.M33, l = m.M34;
            double mm = m.M41, n = m.M42, o = m.M43, p = m.M44;

            double kpLo = k * p - l * o, jpLn = j * p - l * n, joKn = j * o - k * n;
            double ipLm = i * p - l * mm, ioKm = i * o - k * mm, inJm = i * n - j * mm;
            double a11 = f * kpLo - g * jpLn + h * joKn;
            double a12 = -(e * kpLo - g * ipLm + h * ioKm);
            double a13 = e * jpLn - f * ipLm + h * inJm;
            double a14 = -(e * joKn - f * ioKm + g * inJm);
            double det = a * a11 + b * a12 + c * a13 + d * a14;
            if (!(Math.Abs(det) > 0.0) || !double.IsFinite(det))
            {
                inverse = new DoubleMatrix4x4(double.NaN, double.NaN, double.NaN, double.NaN,
                    double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                    double.NaN, double.NaN, double.NaN, double.NaN);
                return false;
            }

            double s = 1.0 / det;
            double gpHo = g * p - h * o, fpHn = f * p - h * n, foGn = f * o - g * n;
            double epHm = e * p - h * mm, eoGm = e * o - g * mm, enFm = e * n - f * mm;
            double glHk = g * l - h * k, flHj = f * l - h * j, fkGj = f * k - g * j;
            double elHi = e * l - h * i, ekGi = e * k - g * i, ejFi = e * j - f * i;
            inverse = new DoubleMatrix4x4(
                a11 * s, -(b * kpLo - c * jpLn + d * joKn) * s, (b * gpHo - c * fpHn + d * foGn) * s,
                -(b * glHk - c * flHj + d * fkGj) * s,
                a12 * s, (a * kpLo - c * ipLm + d * ioKm) * s, -(a * gpHo - c * epHm + d * eoGm) * s,
                (a * glHk - c * elHi + d * ekGi) * s,
                a13 * s, -(a * jpLn - b * ipLm + d * inJm) * s, (a * fpHn - b * epHm + d * enFm) * s,
                -(a * flHj - b * elHi + d * ejFi) * s,
                a14 * s, (a * joKn - b * ioKm + c * inJm) * s, -(a * foGn - b * eoGm + c * enFm) * s,
                (a * fkGj - b * ekGi + c * ejFi) * s);
            return true;
        }
    }
}
