using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuFilterProbe
{
    internal static string Source => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuArithmeticGlsl.Source + CoverageGpuRoundingGlsl.Source
        + CoverageGpuFilterBallGlsl.Source + CoverageGpuFilterJobsGlsl.Source + """
        vec3 point(uint s) { return vec3(uintBitsToFloat(Inputs[s]),uintBitsToFloat(Inputs[s+1u]),uintBitsToFloat(Inputs[s+2u])); }
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u+row*13u, d = row*6u;
            CovFilteredJob job = covJob(int(Inputs[s]),point(s+1u),point(s+4u),point(s+7u),point(s+10u));
            CovFilteredResult result = covFilteredWorker(job);
            Results[d] = CovFilterRefused ? 1u : 0u;
            Results[d+1u] = uint(result.predicate.sign); Results[d+2u] = floatBitsToUint(result.predicate.value);
            Results[d+3u] = floatBitsToUint(result.exact.hi); Results[d+4u] = floatBitsToUint(result.exact.lo);
            Results[d+5u] = floatBitsToUint(result.exact.radius);
        }
        """;
}
