using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal sealed record D3D11DiagnosticOptions(bool Reflection, D3D11RecordMode RecordMode, bool DebugLayer, bool Invocations)
{
    internal GpuEffectiveDiagnosticConfiguration? Effective { get; init; }
    internal string Configuration => $"reflection={Reflection},record={RecordMode},debugLayer={DebugLayer},invocations={Invocations}";
    internal static D3D11DiagnosticOptions FromEnvironment(D3D11RecordMode mode, bool debugLayer)
        => new(Environment.GetEnvironmentVariable("KE_D3D11_PIPELINE_DIAGNOSTICS") == "1", mode, debugLayer,
            Environment.GetEnvironmentVariable("KE_D3D11_INVOCATION_DIAGNOSTICS") == "1");
}

internal readonly record struct D3D11ShaderCount(string Name, string Description, uint Value);
internal sealed record D3D11ShaderReflection(string Compiler, ImmutableArray<D3D11ShaderCount> Statistics)
{
    internal ImmutableArray<GpuDiagnosticFailure> Failures { get; init; } = [];
}

/// <summary>Only the native reflection operation is replaceable. Hashing, support and ownership stay real.</summary>
internal interface ID3D11ShaderReflectionApi
{
    D3D11ShaderReflection Read(ReadOnlySpan<byte> dxbc, int maximumCreatorUtf8Bytes);
}

internal sealed record D3D11ShaderEvidence(string Source, string Artifact, string Stage, string Flags,
    string Configuration, GpuDiagnosticStatus Status, string? Reason, ImmutableArray<GpuPipelineStatistic> Statistics)
{
    internal GpuEffectiveDiagnosticConfiguration? Effective { get; init; }
    internal bool ReflectionRequested { get; init; }
    internal bool CountsComplete { get; init; }
    internal GpuPipelineStageIdentity StageIdentity { get; init; } = null!;
    internal GpuDiagnosticDigest CodeGeneration { get; init; } = null!;
    internal ImmutableArray<GpuDiagnosticOption> CodeGenerationOptions { get; init; } = [];
    internal ImmutableArray<GpuDiagnosticFailure> Failures { get; init; } = [];

    internal static D3D11ShaderEvidence Capture(string source, ReadOnlySpan<byte> dxbc, string stage, uint flags,
        D3D11DiagnosticOptions options, ID3D11ShaderReflectionApi api)
    {
        string sourceHash = Hash(Encoding.UTF8.GetBytes(source));
        string profile = stage switch { "vertex" => "vs_5_0", "pixel" => "ps_5_0", "compute" => "cs_5_0", _ => throw new ArgumentOutOfRangeException(nameof(stage)) };
        string compilerFlags = $"D3DCompile Flags1=0x{flags:X8},Flags2=0,entry=main,profile={profile};"
            + KhaozEngine.Gpu.Internal.SpirvFrontEndPin.Identity + ";" + KhaozEngine.Gpu.Internal.HlslCrossCompilePin.Identity;
        compilerFlags += ";diagnostics=" + options.Configuration;
        string configuration = options.Configuration + "," + compilerFlags;
        var stageIdentity = new GpuPipelineStageIdentity(stage, "main",
            KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Text(source, "glsl.utf8/1"), null,
            KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Bytes(dxbc, "dxbc.bytes/1"));
        ImmutableArray<GpuDiagnosticOption> codeGenerationOptions = [new("d3d.flags1", flags.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)), new("d3d.flags2", "0"),
                new("d3d.profile", profile), new("entry", "main"), new("frontend", KhaozEngine.Gpu.Internal.SpirvFrontEndPin.Identity),
                new("hlsl", KhaozEngine.Gpu.Internal.HlslCrossCompilePin.Identity), new("toolchain", KhaozEngine.Gpu.Internal.SpirvToolchainVersion.Identity)];
        var codeGeneration = KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.Direct3D11Native, codeGenerationOptions);
        D3D11ShaderEvidence Evidence(GpuDiagnosticStatus status, string? reason, ImmutableArray<GpuPipelineStatistic> statistics,
            string? rawFlags = null, string? rawConfiguration = null, ImmutableArray<GpuDiagnosticFailure> failures = default, bool countsComplete = false)
            => new(sourceHash, stageIdentity.ShaderArtifactSha256.Sha256 ?? "", stage, rawFlags ?? compilerFlags,
                rawConfiguration ?? configuration, status, reason, statistics)
            { Effective = options.Effective, ReflectionRequested = options.Reflection, CountsComplete = countsComplete, StageIdentity = stageIdentity, CodeGeneration = codeGeneration, CodeGenerationOptions = codeGenerationOptions, Failures = failures.IsDefault ? [] : failures };
        if (dxbc.IsEmpty)
        {
            var error = new InvalidOperationException("The compiled DXBC module is empty.");
            return Evidence(GpuDiagnosticStatus.Invalid, error.Message, [], failures: [new("d3d11.shader-artifact", error)]);
        }
        if (!options.Reflection) return Evidence(GpuDiagnosticStatus.Unsupported, "Pipeline reflection was not requested on this device.", []);
        try
        {
            D3D11ShaderReflection reflection = api.Read(dxbc, 4096);
            if (Encoding.UTF8.GetByteCount(reflection.Compiler) > 8192 || reflection.Statistics.IsDefault || reflection.Statistics.Length > 30)
                throw new InvalidOperationException("D3D reflection exceeds its fixed text/count profile.");
            string rawFlags = compilerFlags + ";" + reflection.Compiler;
            if (KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(rawFlags) > 16384)
                throw new InvalidOperationException("CompilerFlags exceeds 16384 UTF-8 bytes.");
            foreach (var c in reflection.Statistics)
                if (KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(c.Name) > 256
                    || KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(c.Description) > 256)
                    throw new InvalidOperationException("D3D fixed statistic text exceeds its profile.");
            return Evidence(!reflection.Failures.IsEmpty ? GpuDiagnosticStatus.Invalid
                    : reflection.Statistics.IsEmpty ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
                !reflection.Failures.IsEmpty ? "Compiler observation failed. Actual exception retained in Failures."
                    : reflection.Statistics.IsEmpty ? "D3DReflect returned no shader statistics." : null,
                reflection.Statistics.Select(c => new GpuPipelineStatistic(c.Name, c.Description,
                    new(new(GpuDiagnosticValueType.UInt64, UInt64: c.Value), c.Name == "TempRegisterCount" ? "slots" : "count",
                        "D3D11_SHADER_DESC." + c.Name + " (UINT)", GpuDiagnosticStatus.Available, null,
                        GpuDiagnosticProvenance.IntermediateCode))).ToImmutableArray(), rawFlags, configuration + "," + reflection.Compiler, reflection.Failures, countsComplete: true);
        }
        catch (Exception error)
        {
            var failures = error is GpuDiagnosticFailureException transport ? transport.Failures
                : ImmutableArray.Create(new GpuDiagnosticFailure("d3d11.shader-reflection", error));
            return Evidence(GpuDiagnosticStatus.Invalid, "D3DReflect failed. Actual exception retained in Failures.", [], failures: failures);
        }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
