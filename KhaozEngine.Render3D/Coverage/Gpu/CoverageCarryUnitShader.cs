namespace KhaozEngine.Render3D;

/// <summary>Native direction arithmetic, separate from the exact position graph.</summary>
internal static class CoverageCarryUnitShader
{
    internal const string Source = """
        vec3 covDirectionRead3(uint start) {
            return vec3(uintBitsToFloat(CovWorkWords[start]),uintBitsToFloat(CovWorkWords[start+1u]),uintBitsToFloat(CovWorkWords[start+2u]));
        }
        void covDirectionWrite3(uint start, vec3 value) {
            CovWorkWords[start] = floatBitsToUint(value.x);
            CovWorkWords[start+1u] = floatBitsToUint(value.y);
            CovWorkWords[start+2u] = floatBitsToUint(value.z);
        }
        bool covDirectionFinite(float value) { return (floatBitsToUint(value) & 0x7f800000u) != 0x7f800000u; }
        bool covDirectionFinite3(vec3 value) {
            return covDirectionFinite(value.x) && covDirectionFinite(value.y) && covDirectionFinite(value.z);
        }
        bool covDirectionZero(vec3 value) { return value.x == 0.0 && value.y == 0.0 && value.z == 0.0; }
        uint covDirectionUnit(vec3 value, out vec3 unit) {
            unit = vec3(0.0);
            float squared = dot(value,value);
            if (!covDirectionFinite3(value) || !covDirectionFinite(squared)) return 4u;
            // CPU dot products flush at or below 2^-126. Keep that zero-length floor without flushing directions.
            if (!(squared > uintBitsToFloat(0x00800000u))) return 8u;
            unit = value/sqrt(squared);
            return covDirectionFinite3(unit) ? 0u : 4u;
        }
        """;
}
