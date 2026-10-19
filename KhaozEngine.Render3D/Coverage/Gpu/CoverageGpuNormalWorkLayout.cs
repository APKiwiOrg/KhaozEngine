namespace KhaozEngine.Render3D;

/// <summary>Checked, face-owned storage between the two basis producers and their immutable consumers.</summary>
internal static class CoverageGpuNormalWorkLayout
{
    internal const int Pair = 42, Words = 46;

    // Twelve basis vectors, raw normals, two determinants, code, state.
    // Each producer initializes code/state before any dependent read. States are idle 0,
    // prepared 1, complete 2 and failed 3. Only state 1 or 2 publishes its payload.
    internal const string Source = """
        struct CovNormalBasis { vec3 u; vec3 v; vec3 n; vec3 s; vec3 t; vec3 m; vec3 bu; vec3 bv; vec3 bn; vec3 au; vec3 av; vec3 an; };
        uint covNormalWork(uint w, uint triangle) { return covRow(w,42u)+triangle*46u; }
        // Call only after checking the producer's state word at p+45u.
        CovNormalBasis covNormalBasisRead(uint p) {
            return CovNormalBasis(covWork3(p),covWork3(p+3u),covWork3(p+6u),covWork3(p+9u),covWork3(p+12u),covWork3(p+15u),
                covWork3(p+18u),covWork3(p+21u),covWork3(p+24u),covWork3(p+27u),covWork3(p+30u),covWork3(p+33u));
        }
        bool covMoved(uint w, uint vertex) {
            return !covEqual3(covWork3(covRow(w,4u)+vertex*16u),covWork3(covRow(w,8u)+vertex*16u));
        }
        vec3 covCombine(vec3 a, float x, vec3 b, float y, vec3 c, float z) {
            return vec3(covAdd(covAdd(covMul(a.x,x),covMul(b.x,y)),covMul(c.x,z)),
                covAdd(covAdd(covMul(a.y,x),covMul(b.y,y)),covMul(c.y,z)),covAdd(covAdd(covMul(a.z,x),covMul(b.z,y)),covMul(c.z,z)));
        }
        """;
}
