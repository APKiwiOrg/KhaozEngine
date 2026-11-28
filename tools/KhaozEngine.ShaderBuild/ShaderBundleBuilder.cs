using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KhaozEngine.ShaderBuild;

/// <summary>Compiles every program and variant a manifest lists into the bytes of one bundle.</summary>
public static class ShaderBundleBuilder
{
    /// <summary>Builds in a work folder of its own, so concurrent builds of several projects never share files.</summary>
    public static byte[] Build(string manifestPath, string slangc, string slangcVersion, Func<string, string?> environment, TextWriter diagnostics)
    {
        var manifest = ShaderManifest.Load(manifestPath);
        var validator = SpirvValidator.Find(environment);
        var work = Path.Combine(Path.GetTempPath(), $"khaozengine-shaderbuild-{Guid.NewGuid():N}");
        try
        {
            var compiler = new SlangCompiler(slangc, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, work, diagnostics);
            var programs = new List<BundleProgram>();
            foreach (var program in manifest.Programs)
                foreach (var variant in program.Variants)
                    programs.Add(BuildVariant(compiler, validator, program, variant));
            return BundleWriter.Write(slangcVersion, programs);
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    private static BundleProgram BuildVariant(SlangCompiler compiler, string? validator, ManifestProgram program, ManifestVariant variant)
    {
        try
        {
            var (vertex, bindings) = BuildStage(compiler, validator, program.Source, program.Vertex, "vertex", variant, program.VertexPreciseMath);
            var (fragment, fragmentBindings) = BuildStage(compiler, validator, program.Source, program.Fragment, "fragment", variant, preciseMath: false);
            if (!bindings.SequenceEqual(fragmentBindings))
                throw new ShaderBuildException(
                    ShaderBuildFailure.ManifestInvalid, "The vertex and fragment reflection give different bindings.");
            return new BundleProgram(program.Id, variant.Name, program.VertexPreciseMath, bindings, vertex, fragment);
        }
        catch (ShaderBuildException refusal) when (refusal.ProgramId is null)
        {
            throw new ShaderBuildException(
                refusal.Failure,
                $"Shader program '{program.Id}' variant '{variant.Name}': {refusal.Message}",
                refusal.AssetUrl,
                refusal.OverrideVariable,
                refusal)
            { ProgramId = program.Id, Parameter = refusal.Parameter };
        }
    }

    private static (BundleStage Stage, IReadOnlyList<BundleBinding> Bindings) BuildStage(
        SlangCompiler compiler, string? validator, string source, string entryPoint, string stage, ManifestVariant variant, bool preciseMath)
    {
        var spirv = compiler.Compile(source, entryPoint, stage, SlangTarget.Spirv, variant.Defines, preciseMath);
        if (validator is not null)
            SpirvValidator.Validate(validator, spirv.OutputPath);
        var metal = compiler.Compile(source, entryPoint, stage, SlangTarget.Metal, variant.Defines, preciseMath);
        var hlsl = compiler.Compile(source, entryPoint, stage, SlangTarget.Hlsl, variant.Defines, preciseMath);
        var msl = RequireEntryPoint(entryPoint, SlangTarget.Metal, metal.Text);
        var hlslText = RequireEntryPoint(entryPoint, SlangTarget.Hlsl, hlsl.Text);
        return (new BundleStage(entryPoint, spirv.Code, msl, hlslText), SlangReflection.Join(spirv.Reflection, metal.Reflection, hlsl.Reflection));
    }

    // slangc renames every SPIR-V entry point to main but keeps the declared name in MSL and HLSL unless it
    // collides with a target keyword, and the runtime compiles those by the declared name.
    private static string RequireEntryPoint(string entryPoint, SlangTarget target, string code)
        => Regex.IsMatch(code, $@"\b{entryPoint}\s*\(")
            ? code
            : throw new ShaderBuildException(
                ShaderBuildFailure.ManifestInvalid,
                $"slangc renamed entry point '{entryPoint}' in its {target} output. Choose a name the target does not reserve.");
}
