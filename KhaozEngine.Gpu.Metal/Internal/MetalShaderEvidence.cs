using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Exact available GLSL and emitted MSL identity. MSL is source, never a native compiler artifact.</summary>
internal sealed record MetalShaderEvidence(MetalShaderStage Stage, string EntryPoint, string SourceSha256,
    string Msl, string Configuration)
{
    internal static MetalShaderEvidence Capture(MetalMslStage stage, string glsl)
    {
        var validation = MetalValidationReader.Current();
        string configuration = MslCompilePin.Identity + ";" + SpirvFrontEndPin.Identity + ";"
            + MslCrossCompilePin.Identity + ";" + SpirvToolchainVersion.Identity
            + ";capture=none;reflection=none;nativeCompilerVersion=unavailable;debugLayer="
            + validation.DebugLayerArmed + ";shaderValidation=" + validation.ShaderValidationArmed
            + ";os=" + System.Environment.OSVersion.VersionString;
        return new(stage.Stage, stage.EntryPointName, GpuDiagnosticIdentityEncoding.Text(glsl, "glsl.utf8/1").Sha256 ?? "", stage.Msl, configuration);
    }
}
