using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageCarryKernel(string Name, string Source);

/// <summary>Five-stage carry with exact positions, position-only actor failure and native local direction recovery.</summary>
internal static class CoverageCarryShaders
{
    internal const int StageCount = 5;
    internal static readonly GpuShaderCompileOptions Options = new(PreciseMath: true);
    internal static IReadOnlyList<CoverageCarryKernel> Kernels { get; } = Array.AsReadOnly(new[]
    {
        new CoverageCarryKernel("CoverageCarrySkin", Prefix(1, 0) + CoverageCarrySkinShader.Source),
        new CoverageCarryKernel("CoverageCarryTransport", Prefix(3, 1) + CoverageCarryTransportShader.Source),
        new CoverageCarryKernel("CoverageCarryBasis", Prefix(4, 2) + CoverageCarryBasisShader.Source),
        new CoverageCarryKernel("CoverageCarryGather", Prefix(3, 3) + CoverageCarryGatherShader.Source),
        new CoverageCarryKernel("CoverageCarryPublish", Prefix(3, 4) + CoverageCarryPublicationShader.Source),
    });

    static string Prefix(int stride, int stage) => "#version 450\nlayout(local_size_x = 64) in;\n" + Bindings
        + (stage == 1 ? "layout(std430, set = 0, binding = 4) readonly buffer CarryTableBuf { uint CarryTableWords[]; };\n"
            : stage == 4 ? "layout(std430, set = 0, binding = 4) buffer CovCurrentBuf { uint CovCurrentWords[]; };\n" : "")
        + CoverageCarryArithmeticShader.Source + CoverageCarryScalarShader.Source
        + "uint carryStride() { return CovFrameWords[" + stride + "u]; }\n"
        + Helpers;

    const string Bindings = """
        layout(std430, set = 0, binding = 0) readonly buffer CovStaticBuf { uint CovStaticWords[]; };
        layout(std430, set = 0, binding = 1) readonly buffer CovFrameBuf { uint CovFrameWords[]; };
        layout(std430, set = 0, binding = 2) buffer CovWorkBuf { uint CovWorkWords[]; };
        layout(std430, set = 0, binding = 3) buffer CovResultsBuf { uint CovResultWords[]; };
        """;

    const string Helpers = """
        uint carryActor() { return gl_GlobalInvocationID.x/carryStride(); }
        uint carryIndex() { return gl_GlobalInvocationID.x%carryStride(); }
        uint covFrame() { return CovFrameWords[5u+carryActor()]; }
        uint covTable(uint host, uint pair) { return host+CovStaticWords[host+pair]; }
        uint covRow(uint work, uint pair) { return work+CovWorkWords[work+pair]; }
        vec3 covWork3(uint start) {
            return vec3(covRead(CovWorkWords[start]),covRead(CovWorkWords[start+1u]),covRead(CovWorkWords[start+2u]));
        }
        void covWrite3(uint start, vec3 value) {
            CovWorkWords[start] = floatBitsToUint(covF(value.x));
            CovWorkWords[start+1u] = floatBitsToUint(covF(value.y));
            CovWorkWords[start+2u] = floatBitsToUint(covF(value.z));
        }
        bool covCompletedFailure(uint work) { return CovResultWords[CovFrameWords[covFrame()+4u]+6u] != 0u; }
        uint covCommands(uint f) { return f+16u+CovFrameWords[f+11u]*16u; }
        uint covCommand(uint f, uint slot) { return covCommands(f)+slot*8u; }
        int covRegionSlot(uint f, int region) {
            return region < 0 ? -1 : int(CovFrameWords[covCommands(f)+32u+uint(region)]);
        }
        uint covVertex(uint h, uint v) { return covTable(h,12u)+v*10u; }
        vec3 covStaticDirection3(uint start) {
            return vec3(uintBitsToFloat(CovStaticWords[start]),uintBitsToFloat(CovStaticWords[start+1u]),uintBitsToFloat(CovStaticWords[start+2u]));
        }
        vec3 covAuthoredDirection(uint h, uint vertex, bool tangent) {
            uint r = covVertex(h,vertex); int original = int(CovStaticWords[r+1u]);
            uint source = original >= 0 ? uint(original) : CovStaticWords[covTable(h,16u)+CovStaticWords[r]*8u+5u];
            return covStaticDirection3(covTable(h,8u)+source*20u+(tangent ? 12u : 3u));
        }
        vec3 covRecess(uint command) {
            return vec3(covRead(CovFrameWords[command]),covRead(CovFrameWords[command+1u]),covRead(CovFrameWords[command+2u]));
        }
        vec3 covMove(vec3 before, float full, vec3 recess) {
            return vec3(covSub(before.x,covMul(full,recess.x)),covSub(before.y,covMul(full,recess.y)),covSub(before.z,covMul(full,recess.z)));
        }
        """;
}
