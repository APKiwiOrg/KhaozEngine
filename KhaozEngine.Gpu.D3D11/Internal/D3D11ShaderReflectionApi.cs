using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.D3DCompiler;
using Vortice.Direct3D11.Shader;

namespace KhaozEngine.Gpu.D3D11.Internal;

[SupportedOSPlatform("windows")]
internal sealed class D3D11ShaderReflectionApi : ID3D11ShaderReflectionApi
{
    string? _compiler;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc)
    {
        try { return ReadWindows(dxbc); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new NotSupportedException($"D3DReflect is unavailable: {error.Message}", error);
        }
        catch (SharpGen.Runtime.SharpGenException error)
        {
            throw new InvalidOperationException($"D3DReflect/GetDesc failed (0x{error.HResult:X8}): {error.Message}", error);
        }
    }

    D3D11ShaderReflection ReadWindows(ReadOnlySpan<byte> dxbc)
    {
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(dxbc);
        var d = reflection.Description;
        var statistics = ImmutableArray.CreateBuilder<D3D11ShaderCount>();
        void Count(string name, string description, int rawValue)
            => statistics.Add(new(name, description, unchecked((uint)rawValue)));
        Count("ConstantBuffers", "The number of shader-constant buffers.", d.ConstantBuffers);
        Count("BoundResources", "The number of resource (textures and buffers) bound to a shader.", d.BoundResources);
        Count("InputParameters", "The number of parameters in the input signature.", d.InputParameters);
        Count("OutputParameters", "The number of parameters in the output signature.", d.OutputParameters);
        Count("InstructionCount", "The number of intermediate-language instructions in the compiled shader.", d.InstructionCount);
        Count("TempRegisterCount", "The number of temporary registers in the compiled shader.", d.TempRegisterCount);
        Count("TempArrayCount", "Number of temporary arrays used.", d.TempArrayCount);
        Count("DefCount", "Number of constant defines.", d.DefineCount);
        Count("DclCount", "Number of declarations (input + output).", d.DeclarationCount);
        Count("TextureNormalInstructions", "Number of non-categorized texture instructions.", d.TextureNormalInstructions);
        Count("TextureLoadInstructions", "Number of texture load instructions", d.TextureLoadInstructions);
        Count("TextureCompInstructions", "Number of texture comparison instructions", d.TextureCompInstructions);
        Count("TextureBiasInstructions", "Number of texture bias instructions", d.TextureBiasInstructions);
        Count("TextureGradientInstructions", "Number of texture gradient instructions.", d.TextureGradientInstructions);
        Count("FloatInstructionCount", "Number of floating point arithmetic instructions used.", d.FloatInstructionCount);
        Count("IntInstructionCount", "Number of signed integer arithmetic instructions used.", d.IntInstructionCount);
        Count("UintInstructionCount", "Number of unsigned integer arithmetic instructions used.", d.UintInstructionCount);
        Count("StaticFlowControlCount", "Number of static flow control instructions used.", d.StaticFlowControlCount);
        Count("DynamicFlowControlCount", "Number of dynamic flow control instructions used.", d.DynamicFlowControlCount);
        Count("MacroInstructionCount", "Number of macro instructions used.", d.MacroInstructionCount);
        Count("ArrayInstructionCount", "Number of array instructions used.", d.ArrayInstructionCount);
        Count("CutInstructionCount", "Number of cut instructions used.", d.CutInstructionCount);
        Count("EmitInstructionCount", "Number of emit instructions used.", d.EmitInstructionCount);
        Count("GSMaxOutputVertexCount", "Geometry shader maximum output vertex count.", d.GeometryShaderMaxOutputVertexCount);
        Count("PatchConstantParameters", "Number of parameters in the patch-constant signature.", d.PatchConstantParameters);
        Count("cGSInstanceCount", "Number of geometry shader instances.", d.GeometryShaderInstanceCount);
        Count("cControlPoints", "Number of control points in the hull shader and domain shader.", d.ControlPoints);
        Count("cBarrierInstructions", "Number of barrier instructions in a compute shader.", d.BarrierInstructions);
        Count("cInterlockedInstructions", "Number of interlocked instructions in a compute shader.", d.InterlockedInstructions);
        Count("cTextureStoreInstructions", "Number of texture writes in a compute shader.", d.TextureStoreInstructions);
        return new(($"Creator={d.Creator}," + (_compiler ??= CompilerIdentity())), statistics.ToImmutable());
    }

    static string CompilerIdentity()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    if (!string.Equals(module.ModuleName, "d3dcompiler_47.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    return $"d3dcompiler_47.dll,version={module.FileVersionInfo.FileVersion},sha256="
                        + D3D11ShaderEvidence.Hash(File.ReadAllBytes(module.FileName));
                }
            }
            return "d3dcompiler_47.dll,version=unavailable,reason=loaded module not found";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"d3dcompiler_47.dll,version=unavailable,reason={error.GetType().Name}: {error.Message}";
        }
    }
}
