using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuFilterProbe
{
    internal static string Source => """
        #version 450
        #extension GL_EXT_control_flow_attributes : require
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuArithmeticGlsl.Source + CoverageGpuRoundingGlsl.Source
        + CoverageGpuFilterBallGlsl.Source + CoverageGpuFilterJobsGlsl.Source
        + CoverageGpuPredicatesGlsl.Source[CoverageGpuExactJobsGlsl.Source.Length..]
            .Replace("CovExact", "CovFiltered", StringComparison.Ordinal).Replace("covExact", "covFiltered", StringComparison.Ordinal) + """
        vec3 point(uint s) { return vec3(uintBitsToFloat(Inputs[s]),uintBitsToFloat(Inputs[s+1u]),uintBitsToFloat(Inputs[s+2u])); }
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u+row*14u, d = row*17u;
            [[dont_unroll]] for (uint field = 0u; field < 17u; field++) Results[d+field] = 0u;
            int operation = int(Inputs[s]);
            vec3 a = point(s+1u), b = point(s+4u), c = point(s+7u), e = point(s+10u);
            CovFilterSignOnly = Inputs[s+13u] != 0u;
            CovFilteredJob job = covJob(operation,a,b,c,e);
            CovFilteredResult result = covFilteredResultZero();
            CovCrossingGraph q = covCrossingBegin(a,b,c,e);
            bool done = false;
            [[dont_unroll]] while (!done && !CovFilterRefused) {
                bool request = true;
                if (operation == 2) request = covCrossingStep(q,result,job);
                else done = true;
                if (request) {
                    result = covFilteredWorker(job);
                    if (operation == 2 && q.phase == CovCrossRoundA) Results[d+7u] = result.rounded;
                    if (operation == 2 && q.phase == CovCrossRoundB) Results[d+8u] = result.rounded;
                }
                if (operation == 2 && q.phase == CovCrossDone) done = true;
            }
            CovFiltered first = operation == 2 ? q.ca : result.exact;
            Results[d] = CovFilterRefused ? 1u : 0u;
            Results[d+1u] = uint(operation == 2 ? q.ca.sign : result.predicate.sign);
            Results[d+2u] = floatBitsToUint(operation == 2 ? q.value : result.predicate.value);
            Results[d+3u] = floatBitsToUint(first.hi); Results[d+4u] = floatBitsToUint(first.lo);
            Results[d+5u] = floatBitsToUint(first.radius);
            Results[d+6u] = uint(q.power);
            Results[d+9u] = uint(q.cb.sign);
            Results[d+10u] = floatBitsToUint(q.cb.hi); Results[d+11u] = floatBitsToUint(q.cb.lo);
            Results[d+12u] = floatBitsToUint(q.cb.radius);
            if (operation == 2 && !CovFilterRefused && covFinite(q.value)) {
                // The pinned overlay construction follows the certified B30 parameter.
                precise float u = covClamp(q.value,0.0,1.0);
                vec3 crossingPoint = vec3(covAdd(c.x,covMul(u,covSub(e.x,c.x))),
                    covAdd(c.y,covMul(u,covSub(e.y,c.y))),covAdd(c.z,covMul(u,covSub(e.z,c.z))));
                Results[d+14u] = floatBitsToUint(crossingPoint.x);
                Results[d+15u] = floatBitsToUint(crossingPoint.y);
                Results[d+16u] = floatBitsToUint(crossingPoint.z);
            }
            // A fast sign certificate returns a finite +/-1 token without constructing a ball.
            Results[d+13u] = operation != 2 && CovFilterSignOnly && first.sign == 0 && result.predicate.sign != 0 ? 1u : 0u;
        }
        """;
}
