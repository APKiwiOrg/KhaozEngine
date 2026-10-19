using System.Linq;

namespace KhaozEngine.Render3D;

/// <summary>General wide multiplication for the independent diagnostic probe only.</summary>
internal static class CoverageGpuWideDiagnosticGlsl
{
    internal static string Source => """

        #extension GL_EXT_control_flow_attributes : require
        void covExactDiagnosticStore(inout CovExact a, int index, uint word) {
        """ + string.Join('\n', Enumerable.Range(0, 18).Select(i => $"if (index == {i}) a.limb[{i}] = word;")) + """
        }
        CovExact covExactProduct(CovExact a, CovExact b, out uint overflow) {
            CovExact result = covExactZero();
            overflow = 0u;
            [[dont_unroll]] for (int i = 0; i < 18; i++) {
                uint factor = covExactWord(a,i);
                if (factor == 0u) continue;
                uint carry = 0u;
                [[dont_unroll]] for (int j = 0; j < 18; j++) {
                    uint other = covExactWord(b,j);
                    if (i+j >= 18) { overflow |= other; continue; }
                    uint low, high; covExactMul32(factor,other,low,high);
                    uint sum = covExactWord(result,i+j)+low, next = sum+carry;
                    carry = high+uint(sum < low)+uint(next < sum);
                    covExactDiagnosticStore(result,i+j,next);
                }
                overflow |= carry;
            }
            overflow = uint(overflow != 0u);
            result.sign = covExactBitLength(result) == 0 ? 0 : a.sign*b.sign;
            return result;
        }
        CovExact covExactProduct(CovExact a, CovExact b) { uint overflow; return covExactProduct(a,b,overflow); }

        """;
}
