namespace KhaozEngine.Render3D;

/// <summary>The shared scalar graph for coverage kernels. Compile with PreciseMath enabled.</summary>
internal static class CoverageGpuArithmeticGlsl
{
    internal static string Source => """

        float covF(float value) {
            uint bits = floatBitsToUint(value);
            uint magnitude = bits & 0x7fffffffu;
            return uintBitsToFloat(magnitude <= 0x00800000u ? bits & 0x80000000u : bits);
        }
        float covAdd(float a, float b) {
            a = covF(a); b = covF(b);
            precise float result = a + b;
            return covF(result);
        }
        float covSub(float a, float b) {
            a = covF(a); b = covF(b);
            precise float result = a - b;
            return covF(result);
        }
        float covMul(float a, float b) {
            a = covF(a); b = covF(b);
            precise float result = a * b;
            return covF(result);
        }
        float covAbs(float a) {
            a = covF(a);
            precise float result = uintBitsToFloat(floatBitsToUint(a) & 0x7fffffffu);
            return covF(result);
        }
        float covNeg(float a) {
            a = covF(a);
            precise float result = uintBitsToFloat(floatBitsToUint(a) ^ 0x80000000u);
            return covF(result);
        }
        float covMin(float a, float b) {
            a = covF(a); b = covF(b);
            uint x = floatBitsToUint(a), y = floatBitsToUint(b);
            bool nanA = (x & 0x7fffffffu) > 0x7f800000u;
            bool nanB = (y & 0x7fffffffu) > 0x7f800000u;
            bool zeros = ((x | y) & 0x7fffffffu) == 0u;
            precise float result = nanA ? a : (nanB ? b :
                (zeros ? uintBitsToFloat(x | y) : (a < b ? a : b)));
            return covF(result);
        }
        float covMax(float a, float b) {
            a = covF(a); b = covF(b);
            uint x = floatBitsToUint(a), y = floatBitsToUint(b);
            bool nanA = (x & 0x7fffffffu) > 0x7f800000u;
            bool nanB = (y & 0x7fffffffu) > 0x7f800000u;
            bool zeros = ((x | y) & 0x7fffffffu) == 0u;
            precise float result = nanA ? a : (nanB ? b :
                (zeros ? uintBitsToFloat(x & y) : (a > b ? a : b)));
            return covF(result);
        }
        float covClamp(float value, float lo, float hi) {
            value = covF(value); lo = covF(lo); hi = covF(hi);
            precise float result = value < lo ? lo : (value > hi ? hi : value);
            return covF(result);
        }
        float covRead(uint word) { return covF(uintBitsToFloat(word)); }
        void covStore(inout uint word, float value) { word = floatBitsToUint(covF(value)); }
        bool covFinite(float value) {
            value = covF(value);
            return (floatBitsToUint(value) & 0x7f800000u) != 0x7f800000u;
        }
        bool covLess(float a, float b) { a = covF(a); b = covF(b); return a < b; }
        bool covLessOrEqual(float a, float b) { a = covF(a); b = covF(b); return a <= b; }
        bool covGreater(float a, float b) { a = covF(a); b = covF(b); return a > b; }
        bool covGreaterOrEqual(float a, float b) { a = covF(a); b = covF(b); return a >= b; }
        bool covEqual(float a, float b) { a = covF(a); b = covF(b); return a == b; }
        bool covNotEqual(float a, float b) { a = covF(a); b = covF(b); return !(a == b); }
        float covDot3(vec3 a, vec3 b) {
            precise float result = covAdd(covAdd(covMul(a.x, b.x), covMul(a.y, b.y)), covMul(a.z, b.z));
            return result;
        }
        float covDot2(vec2 a, vec2 b) {
            precise float result = covAdd(covMul(a.x, b.x), covMul(a.y, b.y));
            return result;
        }
        vec3 covCross(vec3 a, vec3 b) {
            precise vec3 result = vec3(covSub(covMul(a.y, b.z), covMul(a.z, b.y)),
                covSub(covMul(a.z, b.x), covMul(a.x, b.z)), covSub(covMul(a.x, b.y), covMul(a.y, b.x)));
            return result;
        }
        vec3 covSub3(vec3 a, vec3 b) {
            precise vec3 result = vec3(covSub(a.x, b.x), covSub(a.y, b.y), covSub(a.z, b.z));
            return result;
        }
        // mat4 is a field container. m[0] holds M11..M14, m[1] holds M21..M24, and so on.
        vec3 covPoint(vec3 p, mat4 m) {
            precise vec3 result = vec3(
                covAdd(covAdd(covAdd(covMul(p.x, m[0][0]), covMul(p.y, m[1][0])), covMul(p.z, m[2][0])), m[3][0]),
                covAdd(covAdd(covAdd(covMul(p.x, m[0][1]), covMul(p.y, m[1][1])), covMul(p.z, m[2][1])), m[3][1]),
                covAdd(covAdd(covAdd(covMul(p.x, m[0][2]), covMul(p.y, m[1][2])), covMul(p.z, m[2][2])), m[3][2]));
            return result;
        }
        vec3 covLinear(vec3 p, mat4 m) {
            precise vec3 result = vec3(
                covAdd(covAdd(covMul(p.x, m[0][0]), covMul(p.y, m[1][0])), covMul(p.z, m[2][0])),
                covAdd(covAdd(covMul(p.x, m[0][1]), covMul(p.y, m[1][1])), covMul(p.z, m[2][1])),
                covAdd(covAdd(covMul(p.x, m[0][2]), covMul(p.y, m[1][2])), covMul(p.z, m[2][2])));
            return result;
        }
        bool covEqual3(vec3 a, vec3 b) { return covEqual(a.x, b.x) && covEqual(a.y, b.y) && covEqual(a.z, b.z); }
        bool covFinite3(vec3 a) { return covFinite(a.x) && covFinite(a.y) && covFinite(a.z); }

        """;
}
