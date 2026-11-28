using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.ShaderBuild;

internal static class Program
{
    private const string Usage =
        "usage: KhaozEngine.ShaderBuild --manifest <programs.json> --out <bundle.json> --pins <slangc.pins.json> [--slangc <path>]";

    private static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Exit 0 on success, 1 when slangc or spirv-val rejects a shader, 2 on any other refusal.</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = ParseOptions(args);
        if (options is null
            || !options.TryGetValue("--manifest", out var manifest)
            || !options.TryGetValue("--out", out var bundle)
            || !options.TryGetValue("--pins", out var pinsPath))
        {
            error.WriteLine(Usage);
            return 2;
        }
        try
        {
            var pins = SlangcPins.Load(pinsPath);
            var slangc = SlangcResolver.Resolve(
                pins,
                options.GetValueOrDefault("--slangc") ?? Environment.GetEnvironmentVariable(SlangcResolver.OverrideVariable),
                SlangcResolver.DefaultCacheRoot());
            var bytes = ShaderBundleBuilder.Build(manifest, slangc, pins.Version, Environment.GetEnvironmentVariable, error);
            WriteReplacing(bundle, bytes);
            output.WriteLine($"Wrote {bundle}");
            return 0;
        }
        catch (ShaderBuildException refusal)
        {
            error.WriteLine($"error {refusal.Failure}: {refusal.Message}");
            return refusal.Failure == ShaderBuildFailure.CompileFailed ? 1 : 2;
        }
    }

    private static Dictionary<string, string>? ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
            return null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--manifest" or "--out" or "--pins" or "--slangc") || !options.TryAdd(args[i], args[i + 1]))
                return null;
        return options;
    }

    // A rename means a reader, MSBuild's up-to-date check included, never sees a half-written bundle.
    private static void WriteReplacing(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
