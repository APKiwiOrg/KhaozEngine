using System;

namespace KhaozEngine.Imaging
{
    /// <summary>
    /// The 8x8 forward DCT of ITU T.81 section A.3.3 by the Arai, Agui and Nakajima factorization, computed
    /// separably (rows, then columns) in float. Its outputs carry a per-coefficient scale of
    /// <c>8 * Scale[u] * Scale[v]</c> over the orthonormal DCT, which <see cref="Reciprocals"/> folds into the
    /// quantization step so the transform itself needs only 5 multiplies per 8 samples.
    /// </summary>
    internal static class JpegForwardDct
    {
        // Scale[k] = sqrt(2) * cos(k pi / 16) for k > 0, and 1 for k = 0.
        static readonly double[] Scale =
        {
            1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379,
        };

        /// <summary>
        /// The multipliers that quantize this transform's output against <paramref name="quantization"/>
        /// (natural order): one over the quantizer times the coefficient's AAN scale.
        /// </summary>
        internal static float[] Reciprocals(byte[] quantization)
        {
            var reciprocals = new float[64];
            for (int v = 0; v < 8; v++)
            {
                for (int u = 0; u < 8; u++)
                    reciprocals[v * 8 + u] = (float)(1.0 / (quantization[v * 8 + u] * Scale[v] * Scale[u] * 8.0));
            }
            return reciprocals;
        }

        /// <summary>Transforms a level-shifted 8x8 block in place, natural (row-major) order in and out.</summary>
        internal static void Transform(Span<float> block)
        {
            if (block.Length < 64) throw new ArgumentException("an 8x8 block needs 64 samples.", nameof(block));
            for (int row = 0; row < 64; row += 8) Pass(block, row, 1);
            for (int column = 0; column < 8; column++) Pass(block, column, 8);
        }

        // One eight-point AAN butterfly over block[start], block[start + stride], ... block[start + 7 * stride].
        static void Pass(Span<float> d, int start, int stride)
        {
            int i0 = start, i1 = i0 + stride, i2 = i1 + stride, i3 = i2 + stride;
            int i4 = i3 + stride, i5 = i4 + stride, i6 = i5 + stride, i7 = i6 + stride;

            float tmp0 = d[i0] + d[i7], tmp7 = d[i0] - d[i7];
            float tmp1 = d[i1] + d[i6], tmp6 = d[i1] - d[i6];
            float tmp2 = d[i2] + d[i5], tmp5 = d[i2] - d[i5];
            float tmp3 = d[i3] + d[i4], tmp4 = d[i3] - d[i4];

            float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3;
            float tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
            d[i0] = tmp10 + tmp11;
            d[i4] = tmp10 - tmp11;
            float z1 = (tmp12 + tmp13) * 0.707106781f;
            d[i2] = tmp13 + z1;
            d[i6] = tmp13 - z1;

            tmp10 = tmp4 + tmp5;
            tmp11 = tmp5 + tmp6;
            tmp12 = tmp6 + tmp7;
            float z5 = (tmp10 - tmp12) * 0.382683433f;
            float z2 = 0.541196100f * tmp10 + z5;
            float z4 = 1.306562965f * tmp12 + z5;
            float z3 = tmp11 * 0.707106781f;
            float z11 = tmp7 + z3, z13 = tmp7 - z3;
            d[i5] = z13 + z2;
            d[i3] = z13 - z2;
            d[i1] = z11 + z4;
            d[i7] = z11 - z4;
        }
    }
}
