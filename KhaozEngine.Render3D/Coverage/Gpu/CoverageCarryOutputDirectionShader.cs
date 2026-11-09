namespace KhaozEngine.Render3D;

/// <summary>Native final output normalization. Decisions and all inputs to later decisions stay retained.</summary>
internal static class CoverageCarryOutputDirectionShader
{
    internal const string Source = """
        bool covDirectionFinite(float value) { return (floatBitsToUint(value) & 0x7f800000u) != 0x7f800000u; }
        bool covDirectionFinite3(vec3 value) {
            return covDirectionFinite(value.x) && covDirectionFinite(value.y) && covDirectionFinite(value.z);
        }
        uint covDirectionUnit(vec3 value, out vec3 unit) {
            unit = vec3(0.0);
            float squared = covDot3(value,value);
            if (!covFinite3(value) || !covFinite(squared)) return 4u;
            // CPU sqrt is finite and positive iff this retained, per-product-flushed dot is finite and positive.
            if (!covGreater(squared,0.0)) return 8u;
            precise float divisor = sqrt(squared);
            precise vec3 outputValue = value/divisor;
            unit = outputValue;
            // Defence only: exact acceptance bounds every quotient below 2 and the divisor is normal and positive.
            return covDirectionFinite3(unit) ? 0u : 4u;
        }
        """;
}
