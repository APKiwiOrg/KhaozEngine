using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Exact available GLSL and emitted MSL identity. MSL is source, never a native compiler artifact.</summary>
internal sealed record MetalShaderEvidence(MetalShaderStage Stage, string EntryPoint, string SourceSha256,
    string Msl, string Configuration)
{
    internal GpuEffectiveDiagnosticConfiguration? Effective { get; init; }
    internal static MetalShaderEvidence Capture(MetalMslStage stage, string glsl, GpuEffectiveDiagnosticConfiguration? effective = null)
    {
        var validation = effective is null ? MetalValidationReader.Current() : default;
        string configuration = MslCompilePin.Identity + ";" + SpirvFrontEndPin.Identity + ";"
            + MslCrossCompilePin.Identity + ";" + SpirvToolchainVersion.Identity
            + ";capture=none;reflection=none;nativeCompilerVersion=unavailable;debugLayer="
            + (effective?.DebugLayerEnabled ?? validation.DebugLayerArmed) + ";shaderValidation=" + (effective?.ValidationEnabled ?? validation.ShaderValidationArmed)
            + ";os=" + System.Environment.OSVersion.VersionString;
        return new(stage.Stage, stage.EntryPointName, GpuDiagnosticIdentityEncoding.Text(glsl, "glsl.utf8/1").Sha256 ?? "", stage.Msl, configuration) { Effective = effective };
    }
}
