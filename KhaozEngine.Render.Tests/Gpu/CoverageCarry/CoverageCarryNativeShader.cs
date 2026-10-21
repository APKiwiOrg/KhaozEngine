using System;
using System.Globalization;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Approximate arithmetic experiment. Shared carry traversal, native float operations, no exact emulation.</summary>
internal static class CoverageCarryNativeShader
{
    internal const string Label = "uncertified native-float cost floor (approximate)";

    internal static string Adapt(string source, string name)
    {
        source = source.ReplaceLineEndings("\n").Replace("#version 450\n",
            "#version 450\n#extension GL_EXT_control_flow_attributes : require\n", StringComparison.Ordinal);
        int start = source.IndexOf("float covF(", StringComparison.Ordinal);
        int end = source.IndexOf("uint carryStride()", StringComparison.Ordinal);
        if (start < 0 || end <= start) throw new InvalidOperationException("Carry arithmetic boundary changed.");
        source = source[..start] + Arithmetic + Jobs + source[end..];
        source = source.Replace(CoverageGpuQueryWorkLayout.Source.ReplaceLineEndings("\n"), "", StringComparison.Ordinal)
            .Replace("CovExactJob", "CarryNativeJob", StringComparison.Ordinal)
            .Replace("CovExactResult", "CarryNativeResult", StringComparison.Ordinal)
            .Replace("covExactResultZero", "carryNativeResultZero", StringComparison.Ordinal)
            .Replace("covScalarJob", "carryNativeJob", StringComparison.Ordinal)
            .Replace("covExactWorker", "carryNativeEvaluate", StringComparison.Ordinal)
            .Replace("precise ", "", StringComparison.Ordinal);
        int[] buffers = CoverageCarryShaders.BufferIndices(name.Replace("CoverageCarry", "CoverageCarryNative", StringComparison.Ordinal));
        // Performance compilation removes unused resources. Compact their bindings for Vulkan as well as
        // the dense reflected layouts used by Metal and D3D11. Original storage ownership stays explicit.
        return Regex.Replace(source, @"(?m)^layout\(std430, set = 0, binding = (\d+)\)[^\n]*", match =>
        {
            int binding = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int compact = Array.IndexOf(buffers, binding);
            return compact < 0 ? "" : match.Value.Replace($"binding = {binding})", $"binding = {compact})", StringComparison.Ordinal);
        });
    }

    const string Jobs = """
        struct CarryNativeValue { float value; };
        struct CarryNativeResult { CarryNativeValue predicate; };
        struct CarryNativeJob { float a; float b; bool root; };
        CarryNativeResult carryNativeResultZero() { return CarryNativeResult(CarryNativeValue(0.0)); }
        CarryNativeJob carryNativeJob(float a, float b, bool root) { return CarryNativeJob(a,b,root); }
        CarryNativeResult carryNativeEvaluate(CarryNativeJob job) {
            return CarryNativeResult(CarryNativeValue(job.root ? sqrt(job.a) : job.a/job.b));
        }

        """;

    const string Arithmetic = """
        float covF(float value) { return value; }
        float covRead(uint word) { return uintBitsToFloat(word); }
        float covAdd(float a, float b) { return a+b; }
        float covSub(float a, float b) { return a-b; }
        float covMul(float a, float b) { return a*b; }
        float covAbs(float a) { return abs(a); }
        float covNeg(float a) { return -a; }
        float covMin(float a, float b) { return min(a,b); }
        float covMax(float a, float b) { return max(a,b); }
        float covClamp(float a, float lo, float hi) { return clamp(a,lo,hi); }
        bool covFinite(float a) { return !isnan(a) && !isinf(a); }
        bool covLess(float a, float b) { return a < b; }
        bool covLessOrEqual(float a, float b) { return a <= b; }
        bool covGreater(float a, float b) { return a > b; }
        bool covGreaterOrEqual(float a, float b) { return a >= b; }
        bool covEqual(float a, float b) { return a == b; }
        bool covNotEqual(float a, float b) { return a != b; }
        float covDot3(vec3 a, vec3 b) { return dot(a,b); }
        vec3 covCross(vec3 a, vec3 b) { return cross(a,b); }
        vec3 covSub3(vec3 a, vec3 b) { return a-b; }
        bool covEqual3(vec3 a, vec3 b) { return all(equal(a,b)); }
        bool covFinite3(vec3 a) { return covFinite(a.x) && covFinite(a.y) && covFinite(a.z); }

        """;
}
