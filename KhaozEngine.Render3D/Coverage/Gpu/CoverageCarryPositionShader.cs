namespace KhaozEngine.Render3D;

/// <summary>Sole actor failure writer. Parameterless and restricted to exact position records and integer topology.</summary>
internal static class CoverageCarryPositionShader
{
    internal const string Source = """
        void covPositionFailures() {
            uint f = covFrame(), w = CovFrameWords[f+1u], vertex = carryIndex();
            uint r = CovFrameWords[f+4u];
            [[dont_unroll]] for (uint source = vertex; source < CovFrameWords[f+5u]; source += CovFrameWords[f+6u]) {
                if (!covFinite3(covWork3(covRow(w,0u)+source*16u))) atomicOr(CovResultWords[r+6u],4u);
            }
            if (!covFinite3(covWork3(covRow(w,4u)+vertex*16u)) || !covFinite3(covWork3(covRow(w,8u)+vertex*16u)))
                atomicOr(CovResultWords[r+6u],4u);
            uint h = CovFrameWords[f], record = covVertex(h,vertex);
            int original = int(CovStaticWords[record+1u]);
            if (original >= 0) {
                uint recipe = covTable(h,22u)+CovStaticWords[record+6u]*7u;
                uint a = CovStaticWords[recipe], b = CovStaticWords[recipe+1u], c = CovStaticWords[recipe+2u];
                uint canonical = CovStaticWords[recipe+3u] == 12u ? a : (CovStaticWords[recipe+4u] == 12u ? b : c);
                if (!covEqual3(covWork3(covRow(w,0u)+uint(original)*16u),covWork3(covRow(w,0u)+canonical*16u)))
                    atomicOr(CovResultWords[r+6u],8u);
            }
        }
        """;
}
