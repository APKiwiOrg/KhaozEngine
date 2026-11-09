namespace KhaozEngine.Render3D;

/// <summary>Native basis and gather helpers. Direction words never feed positions.</summary>
internal static class CoverageCarryNormalShader
{
    internal static string Source => CoverageCarryUnitShader.Source + "\n" + """
        uint covNormalWork(uint w, uint triangle) { return covRow(w,42u)+triangle*46u; }
        bool covMoved(uint w, uint vertex) {
            return CovWorkWords[covRow(w,44u)+vertex] != 0u;
        }
        vec3 covCombine(vec3 a, float x, vec3 b, float y, vec3 c, float z) {
            return (a*x+b*y)+c*z;
        }
        """;
}
