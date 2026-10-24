using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal delegate string D3D11TextDecoder(ReadOnlySpan<byte> value);

/// <summary>Native GetDesc layout. Creator stays a pointer until its bounded, strict UTF-8 validation succeeds.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11ReflectionDescription
{
    internal uint Version;
    internal IntPtr Creator;
    internal uint Flags, ConstantBuffers, BoundResources, InputParameters, OutputParameters, InstructionCount,
        TempRegisterCount, TempArrayCount, DefineCount, DeclarationCount, TextureNormalInstructions,
        TextureLoadInstructions, TextureCompInstructions, TextureBiasInstructions, TextureGradientInstructions,
        FloatInstructionCount, IntInstructionCount, UintInstructionCount, StaticFlowControlCount,
        DynamicFlowControlCount, MacroInstructionCount, ArrayInstructionCount, CutInstructionCount,
        EmitInstructionCount, GeometryShaderOutputTopology, GeometryShaderMaxOutputVertexCount, InputPrimitive,
        PatchConstantParameters, GeometryShaderInstanceCount, ControlPoints, HullShaderOutputPrimitive,
        HullShaderPartitioning, TessellatorDomain, BarrierInstructions, InterlockedInstructions, TextureStoreInstructions;

    internal static unsafe D3D11ReflectionDescription Read(IntPtr self)
    {
        D3D11ReflectionDescription value = default;
        // IUnknown's three methods precede ID3D11ShaderReflection::GetDesc.
        var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, D3D11ReflectionDescription*, int>)(*(void***)self)[3];
        int result = getDesc(self, &value);
        if (result < 0) throw new ExternalException("ID3D11ShaderReflection::GetDesc failed.", result);
        return value;
    }

    internal static unsafe string ReadCreator(IntPtr pointer, int maximumUtf8Bytes, D3D11TextDecoder? decode = null)
    {
        if (maximumUtf8Bytes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        if (pointer == IntPtr.Zero) return "Creator unavailable";
        byte* value = (byte*)pointer;
        int length = 0;
        while (length <= maximumUtf8Bytes && value[length] != 0) length++;
        if (length > maximumUtf8Bytes) throw new InvalidOperationException("D3D Creator exceeds its admitted UTF-8 limit.");
        var bytes = new ReadOnlySpan<byte>(value, length);
        _ = GpuDiagnosticIdentityEncoding.Utf8.GetCharCount(bytes);
        return decode is null ? GpuDiagnosticIdentityEncoding.Utf8.GetString(bytes) : decode(bytes);
    }
}
