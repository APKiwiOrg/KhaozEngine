using System;
using System.ComponentModel;
using System.IO;

namespace KhaozEngine.ShaderBuild;

/// <summary>Finds spirv-val and runs it on each SPIR-V output against Vulkan 1.3.</summary>
public static class SpirvValidator
{
    public const string PathVariable = "SPIRV_VAL";
    public const string RequireVariable = "KHAOZ_REQUIRE_SPIRV_VAL";

    /// <summary>Returns SPIRV_VAL, else spirv-val on PATH, else null unless KHAOZ_REQUIRE_SPIRV_VAL=1 demands one.</summary>
    public static string? Find(Func<string, string?> environment)
    {
        var configured = environment(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured)
                ? configured
                : throw Missing($"{PathVariable} names {configured}, which does not exist.");
        var name = OperatingSystem.IsWindows() ? "spirv-val.exe" : "spirv-val";
        foreach (var folder in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return environment(RequireVariable) == "1"
            ? throw Missing($"spirv-val is not on PATH and {RequireVariable}=1 requires it. Install SPIRV-Tools or set {PathVariable}.")
            : null;
    }

    /// <summary>Refuses SPIR-V that spirv-val rejects, passing its diagnostics through.</summary>
    public static void Validate(string validator, string spirvPath)
    {
        try
        {
            var (exitCode, output, error) = ToolProcess.Run(validator, ["--target-env", "vulkan1.3", spirvPath]);
            if (exitCode != 0)
                throw new ShaderBuildException(
                    ShaderBuildFailure.CompileFailed, $"spirv-val rejected the SPIR-V slangc emitted:\n{(error + output).TrimEnd()}");
        }
        catch (Win32Exception problem)
        {
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolchainMissing, $"Could not run spirv-val at {validator}: {problem.Message}", innerException: problem);
        }
    }

    private static ShaderBuildException Missing(string message)
        => new(ShaderBuildFailure.ToolchainMissing, message, overrideVariable: PathVariable);
}
