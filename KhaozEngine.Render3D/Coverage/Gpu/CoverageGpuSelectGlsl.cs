namespace KhaozEngine.Render3D;

/// <summary>Publishes one complete source or candidate surface from the actor's atomic decision.</summary>
internal static class CoverageGpuSelectGlsl
{
    internal const string Source = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], i = gl_GlobalInvocationID.x;
            uint current = CovFrameWords[f+2u], sourceCount = CovFrameWords[f+5u], vertexCount = CovFrameWords[f+6u];
            uint refused = CovWorkWords[w+30u], r = CovFrameWords[f+4u];
            uint previous = CovFrameWords[f+3u];
            bool changed = (CovFrameWords[f+13u] & 1u) == 0u;
            if (!changed) changed = CovPreviousWords[previous+14u] != refused || CovPreviousWords[previous+8u] != CovResultWords[r+8u];
            uint transition = changed ? 1u : 0u;
            if (i < sourceCount) {
                uint source = covRow(w,0u)+i*16u, target = current+16u+i*16u;
                [[dont_unroll]] for (uint field = 0u; field < 16u; field++)
                    CovCurrentWords[target+field] = floatBitsToUint(covRead(CovWorkWords[source+field]));
            }
            if (i < vertexCount) {
                uint candidate = covRow(w,10u)+i*16u, target = current+16u+(sourceCount+i)*16u;
                [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                    uint value = 0u;
                    if (refused == 0u) value = CovWorkWords[candidate+field];
                    CovCurrentWords[target+field] = value;
                }
            }
            if (i == 0u) {
                CovResultWords[r+14u] = refused;
                CovResultWords[r+15u] = transition;
                [[dont_unroll]] for (uint field = 0u; field < 16u; field++) CovCurrentWords[current+field] = CovResultWords[r+field];
            }
        }
        """;
}
