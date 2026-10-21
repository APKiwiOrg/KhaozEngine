using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Refine the invocation's group before its attributes, using the same single scalar worker pump.</summary>
internal static class CoverageCarryRefinementShader
{
    internal static string Source => Adapt(CoverageGpuAttributesGlsl.Source);

    internal static string Adapt(string source)
    {
        source = source.ReplaceLineEndings("\n");
        source = Change(source, "void main()", "void carryAttributes()");
        source = Change(source,
            "uint target = covRow(w,32u)+i*16u, group = covRow(w,12u)+CovStaticWords[r+6u]*3u;\n    covWrite3(target,covWork3(group));",
            PositionSetup);
        source = Change(source, "while (valid && field < 12u)", "while (valid && (positionAxis < 3u || field < 12u))");
        source = Change(source, "if (direction < 2) {", PositionStep + " else if (direction < 2) {");
        return source;
    }

    static string Change(string source, string before, string after) => source.Contains(before.ReplaceLineEndings("\n"), StringComparison.Ordinal)
        ? source.Replace(before.ReplaceLineEndings("\n"), after.ReplaceLineEndings("\n"), StringComparison.Ordinal)
        : throw new InvalidOperationException("Pinned refinement graph changed.");

    const string PositionSetup = """
        uint target = covRow(w,4u)+i*16u, positionGroup = CovStaticWords[r+6u];
        uint recipe = covTable(h,22u)+positionGroup*7u, positionWeights = covTable(h,24u)+positionGroup*3u;
        uint pa = CovStaticWords[recipe], pb = CovStaticWords[recipe+1u], pc = CovStaticWords[recipe+2u];
        uint wa = CovStaticWords[recipe+3u], wb = CovStaticWords[recipe+4u], wc = CovStaticWords[recipe+5u];
        bool positionOriginal = wa == 12u || wb == 12u || wc == 12u;
        bool positionEdge = !positionOriginal && (wa == 0u || wb == 0u || wc == 0u);
        if (positionOriginal) pa = wa == 12u ? pa : (wb == 12u ? pb : pc);
        if (positionEdge) {
            if (wb == 0u) pb = pc;
            else if (wa == 0u) { pa = pb; pb = pc; }
            if (pa > pb) { uint swap = pa; pa = pb; pb = swap; }
        }
        float positionT = covRead(CovStaticWords[covTable(h,28u)+positionGroup]);
        uint positionAxis = 0u; bool positionPending = false;
        """;

    const string PositionStep = """
        if (positionAxis < 3u) {
            if (positionPending) {
                float value = result.predicate.value;
                CovWorkWords[target+positionAxis] = floatBitsToUint(value);
                if (!covFinite(value)) valid = false;
                positionAxis++; positionPending = false;
            }
            if (positionAxis < 3u) {
                float x = covRead(CovWorkWords[source+pa*16u+positionAxis]), value = x;
                if (positionEdge) value = covAdd(covMul(x,covSub(1.0,positionT)),
                    covMul(covRead(CovWorkWords[source+pb*16u+positionAxis]),positionT));
                else if (!positionOriginal) {
                    float sum = covAdd(covAdd(covMul(x,covRead(CovStaticWords[positionWeights])),
                        covMul(covRead(CovWorkWords[source+pb*16u+positionAxis]),covRead(CovStaticWords[positionWeights+1u]))),
                        covMul(covRead(CovWorkWords[source+pc*16u+positionAxis]),covRead(CovStaticWords[positionWeights+2u])));
                    job = covScalarJob(sum,12.0,false); positionPending = true; request = true;
                }
                if (!request) {
                    CovWorkWords[target+positionAxis] = floatBitsToUint(value);
                    if (!covFinite(value)) valid = false;
                    positionAxis++;
                }
            }
        }
        """;
}
