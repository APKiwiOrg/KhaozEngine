namespace KhaozEngine.Render3D;

/// <summary>Native direction values and local recovery, with no actor failure access.</summary>
internal static class CoverageCarryUnitShader
{
    internal static string Source => CoverageDirectionRecovery.ShaderSource + "\n" + """
        vec3 covDirectionRead3(uint start) {
            return vec3(uintBitsToFloat(CovWorkWords[start]),uintBitsToFloat(CovWorkWords[start+1u]),uintBitsToFloat(CovWorkWords[start+2u]));
        }
        void covDirectionWrite3(uint start, vec3 v) {
            CovWorkWords[start] = floatBitsToUint(v.x); CovWorkWords[start+1u] = floatBitsToUint(v.y); CovWorkWords[start+2u] = floatBitsToUint(v.z);
        }
        float covDirectionDot3(vec3 a, vec3 b) { precise float v = (a.x*b.x+a.y*b.y)+a.z*b.z; return v; }
        vec3 covDirectionCross(vec3 a, vec3 b) {
            precise vec3 v = vec3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x); return v;
        }
        vec3 covDirectionSub(vec3 a, vec3 b) { precise vec3 v = a-b; return v; }
        uint covDirectionUnit(vec3 v, out vec3 unit) {
            unit = vec3(0.0);
            float squared = covDirectionDot3(v,v);
            if (!covDirectionFinite3(v) || !covDirectionFinite(squared)) return 4u;
            if (!(squared > uintBitsToFloat(0x00800000u))) return 8u;
            precise float divisor = sqrt(squared);
            precise vec3 result = v/divisor; unit = result;
            return covDirectionFinite3(unit) ? 0u : 4u;
        }
        """;
}
