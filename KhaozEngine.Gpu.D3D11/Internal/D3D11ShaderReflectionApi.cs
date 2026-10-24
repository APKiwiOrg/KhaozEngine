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
    CompilerObservation? _compiler;
    sealed record CompilerObservation(string Description, ImmutableArray<GpuDiagnosticFailure> Failures);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)
    {
        try { return ReadWindows(dxbc, maximumCreatorUtf8Bytes); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new GpuDiagnosticFailureException([new("d3d11.reflect-native", error)]);
        }
        catch (SharpGen.Runtime.SharpGenException error)
        {
            throw new GpuDiagnosticFailureException([new("d3d11.reflect-native", error)]);
        }
    }

    D3D11ShaderReflection ReadWindows(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes)
    {
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(dxbc);
        var d = D3D11ReflectionDescription.Read(reflection.NativePointer);
        string creator = D3D11ReflectionDescription.ReadCreator(d.Creator, maximumCreatorUtf8Bytes);
        var statistics = D3D11ReflectionStatistics.Read(d);
        var compiler = _compiler ?? CompilerIdentity();
        if (compiler.Failures.IsEmpty) _compiler = compiler;
        if (System.Text.Encoding.UTF8.GetByteCount(compiler.Description) > 4096)
            throw new InvalidOperationException("Compiler description exceeds 4096 UTF-8 bytes.");
        return new(($"Creator={creator}," + compiler.Description), statistics) { Failures = compiler.Failures };
    }

    static CompilerObservation CompilerIdentity()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    if (!string.Equals(module.ModuleName, "d3dcompiler_47.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    return new($"d3dcompiler_47.dll,version={module.FileVersionInfo.FileVersion},sha256="
                        + D3D11ShaderEvidence.Hash(File.ReadAllBytes(module.FileName)), []);
                }
            }
            return new("d3dcompiler_47.dll,version=unavailable,reason=loaded module not found", []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new("d3dcompiler_47.dll,version=unavailable,reason=observation failed", [new("d3d11.compiler-observation", error)]);
        }
    }
}
