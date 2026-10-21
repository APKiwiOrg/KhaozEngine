using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Current and previous words plus tight current bounds. Only genuine reduction dependencies use barriers.</summary>
internal static class CoverageCarryPublicationShader
{
    internal static string FusedSource
    {
        get
        {
            int start = Source.IndexOf("carryGather();", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Carry publication entry changed.");
            int end = Source.IndexOf("ordered =", start, StringComparison.Ordinal);
            if (end <= start) throw new InvalidOperationException("Carry bounds boundary changed.");
            return Source[..start] + "vec3 position; carryGather(position);\n        " + Source[end..];
        }
    }

    internal const string Source = """
        shared uint carryBounds[384];
        uint carryOrdered(float value) {
            uint word = floatBitsToUint(value);
            return (word & 0x80000000u) != 0u ? ~word : word ^ 0x80000000u;
        }
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], vertex = carryIndex(), lane = gl_LocalInvocationID.x;
            uint r = CovFrameWords[f+4u];
            bool live = vertex < CovFrameWords[f+6u];
            uvec3 ordered = uvec3(0u);
            if (live) {
                carryGather();
                uint candidate = covRow(w,10u)+vertex*16u;
                uint current = CovFrameWords[f+2u]+vertex*16u, previous = CovFrameWords[f+3u]+vertex*16u;
                [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                    CovPreviousWords[previous+field] = CovCurrentWords[current+field];
                    CovCurrentWords[current+field] = CovWorkWords[candidate+field];
                }
                vec3 position = covWork3(candidate);
                ordered = uvec3(carryOrdered(position.x),carryOrdered(position.y),carryOrdered(position.z));
            }
            carryBounds[lane] = live ? ordered.x : 0xffffffffu;
            carryBounds[64u+lane] = live ? ordered.y : 0xffffffffu;
            carryBounds[128u+lane] = live ? ordered.z : 0xffffffffu;
            carryBounds[192u+lane] = live ? ordered.x : 0u;
            carryBounds[256u+lane] = live ? ordered.y : 0u;
            carryBounds[320u+lane] = live ? ordered.z : 0u;
            // All 64 lanes, including tails, execute every barrier. There is no return from this entry point.
            barrier();
            [[dont_unroll]] for (uint step = 32u; step > 0u; step >>= 1u) {
                if (lane < step) {
                    [[dont_unroll]] for (uint axis = 0u; axis < 6u; axis++) {
                        uint at = axis*64u+lane, other = carryBounds[at+step];
                        carryBounds[at] = axis < 3u ? min(carryBounds[at],other) : max(carryBounds[at],other);
                    }
                }
                barrier();
            }
            if (lane == 0u) {
                atomicMin(CovResultWords[r],carryBounds[0]); atomicMin(CovResultWords[r+1u],carryBounds[64]);
                atomicMin(CovResultWords[r+2u],carryBounds[128]); atomicMax(CovResultWords[r+3u],carryBounds[192]);
                atomicMax(CovResultWords[r+4u],carryBounds[256]); atomicMax(CovResultWords[r+5u],carryBounds[320]);
            }
            if (vertex == 0u) CovResultWords[r+7u] = CovFrameWords[f+13u];
        }
        """;
}
