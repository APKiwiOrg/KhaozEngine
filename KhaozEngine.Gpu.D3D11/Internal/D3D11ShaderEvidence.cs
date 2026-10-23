using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal sealed record D3D11DiagnosticOptions(bool Reflection, D3D11RecordMode RecordMode, bool DebugLayer, bool Invocations)
{
    internal string Configuration => $"reflection={Reflection},record={RecordMode},debugLayer={DebugLayer},invocations={Invocations}";
    internal static D3D11DiagnosticOptions FromEnvironment(D3D11RecordMode mode, bool debugLayer)
        => new(Environment.GetEnvironmentVariable("KE_D3D11_PIPELINE_DIAGNOSTICS") == "1", mode, debugLayer,
            Environment.GetEnvironmentVariable("KE_D3D11_INVOCATION_DIAGNOSTICS") == "1");
}

internal readonly record struct D3D11ShaderCount(string Name, string Description, uint Value);
internal sealed record D3D11ShaderReflection(string Compiler, ImmutableArray<D3D11ShaderCount> Statistics);

/// <summary>Only the native reflection operation is replaceable. Hashing, support and ownership stay real.</summary>
internal interface ID3D11ShaderReflectionApi
{
    D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc);
}

internal sealed record D3D11ShaderEvidence(string Source, string Artifact, string Stage, string Flags,
    string Configuration, GpuDiagnosticStatus Status, string? Reason, ImmutableArray<GpuPipelineStatistic> Statistics)
{
    internal static D3D11ShaderEvidence Capture(string source, ReadOnlySpan<byte> dxbc, string stage, uint flags,
        D3D11DiagnosticOptions options, ID3D11ShaderReflectionApi api)
    {
        string sourceHash = Hash(Encoding.UTF8.GetBytes(source));
        string artifactHash = Hash(dxbc);
        string profile = stage switch { "vertex" => "vs_5_0", "pixel" => "ps_5_0", "compute" => "cs_5_0", _ => throw new ArgumentOutOfRangeException(nameof(stage)) };
        string compilerFlags = $"D3DCompile Flags1=0x{flags:X8},Flags2=0,entry=main,profile={profile};"
            + KhaozEngine.Gpu.Internal.SpirvFrontEndPin.Identity + ";" + KhaozEngine.Gpu.Internal.HlslCrossCompilePin.Identity;
        compilerFlags += ";diagnostics=" + options.Configuration;
        string configuration = options.Configuration + "," + compilerFlags;
        if (!options.Reflection) return new(sourceHash, artifactHash, stage, compilerFlags, configuration,
            GpuDiagnosticStatus.Unsupported, "Pipeline reflection was not requested on this device.", []);
        if (dxbc.IsEmpty) return new(sourceHash, artifactHash, stage, compilerFlags, configuration,
            GpuDiagnosticStatus.Invalid, "The compiled DXBC module is empty.", []);
        try
        {
            D3D11ShaderReflection reflection = api.Read(dxbc);
            return new(sourceHash, artifactHash, stage, compilerFlags + ";" + reflection.Compiler, configuration + "," + reflection.Compiler,
                reflection.Statistics.IsDefaultOrEmpty ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
                reflection.Statistics.IsDefaultOrEmpty ? "D3DReflect returned no shader statistics." : null,
                reflection.Statistics.IsDefault ? [] : reflection.Statistics.Select(c => new GpuPipelineStatistic(c.Name, c.Description,
                    new(new(GpuDiagnosticValueType.UInt64, UInt64: c.Value), c.Name == "TempRegisterCount" ? "slots" : "count",
                        "D3D11_SHADER_DESC." + c.Name + " (UINT)", GpuDiagnosticStatus.Available, null,
                        GpuDiagnosticProvenance.IntermediateCode))).ToImmutableArray());
        }
        catch (NotSupportedException error)
        {
            return new(sourceHash, artifactHash, stage, compilerFlags, configuration, GpuDiagnosticStatus.Unsupported,
                $"D3DReflect is unsupported: {error.Message}", []);
        }
        catch (Exception error) when (error is InvalidOperationException
            or System.Runtime.InteropServices.ExternalException or System.IO.IOException)
        {
            return new(sourceHash, artifactHash, stage, compilerFlags, configuration, GpuDiagnosticStatus.Invalid,
                $"D3DReflect failed (0x{error.HResult:X8}): {error.Message}", []);
        }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
