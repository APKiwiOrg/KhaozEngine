using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;

namespace KhaozEngine.ShaderBuild;

/// <summary>The targets every program compiles to.</summary>
public enum SlangTarget
{
    Spirv,
    Metal,
    Hlsl,
}

/// <summary>Runs the pinned slangc, one entry point and target per invocation, on LF copies of a manifest folder's Slang sources.</summary>
public sealed class SlangCompiler
{
    private readonly string _slangc;
    private readonly string _sources;
    private readonly string _outputs;
    private readonly TextWriter _diagnostics;
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>Stages every .slang file under the manifest folder into the work folder with CRLF turned into LF.</summary>
    public SlangCompiler(string slangc, string manifestFolder, string workFolder, TextWriter diagnostics)
    {
        _slangc = slangc;
        _sources = Path.Combine(workFolder, "src");
        _outputs = Path.Combine(workFolder, "out");
        _diagnostics = diagnostics;
        Directory.CreateDirectory(_outputs);
        foreach (var original in Directory.EnumerateFiles(manifestFolder, "*.slang", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(manifestFolder, original);
            var staged = Path.Combine(_sources, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllBytes(staged, WithoutCarriageReturns(File.ReadAllBytes(original)));
            _originals[relative] = Path.GetFullPath(original);
        }
    }

    /// <summary>Compiles one entry point for one target, returning the code and its reflection JSON.</summary>
    public SlangOutput Compile(string source, string entryPoint, string stage, SlangTarget target, IReadOnlyDictionary<string, string> defines)
    {
        var number = _next++;
        var output = Path.Combine(_outputs, number + target switch { SlangTarget.Spirv => ".spv", SlangTarget.Metal => ".metal", _ => ".hlsl" });
        var reflection = Path.Combine(_outputs, number + ".json");
        var arguments = new List<string> { source.Replace('\\', '/') };
        arguments.AddRange(target switch
        {
            SlangTarget.Spirv => ["-target", "spirv", "-profile", "spirv_1_3"],
            SlangTarget.Metal => ["-target", "metal"],
            _ => ["-target", "hlsl", "-profile", "sm_5_0"],
        });
        arguments.AddRange(["-matrix-layout-column-major", "-line-directive-mode", "none"]);
        // Visual Studio diagnostics are the file(line,col): error form MSBuild and IDEs recognise.
        arguments.AddRange(["-diagnostic-format", "vs", "-diagnostic-color", "never"]);
        arguments.AddRange(["-entry", entryPoint, "-stage", stage]);
        foreach (var define in defines.OrderBy(d => d.Key, StringComparer.Ordinal))
            arguments.AddRange(["-D", $"{define.Key}={define.Value}"]);
        arguments.AddRange(["-o", output, "-reflection-json", reflection]);

        int exitCode;
        string text;
        try
        {
            var result = ToolProcess.Run(_slangc, arguments, _sources);
            exitCode = result.ExitCode;
            text = MapToOriginals(result.Error + result.Output);
        }
        catch (Win32Exception problem)
        {
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolchainMissing, $"Could not run slangc at {_slangc}: {problem.Message}",
                overrideVariable: SlangcResolver.OverrideVariable, innerException: problem);
        }
        if (exitCode != 0)
            throw new ShaderBuildException(
                ShaderBuildFailure.CompileFailed,
                $"slangc failed compiling {entryPoint} ({stage}) for {target}:\n{text.TrimEnd()}");
        if (text.Trim().Length > 0)
            _diagnostics.WriteLine(text.TrimEnd());
        return new SlangOutput(output, File.ReadAllBytes(output), File.ReadAllText(reflection));
    }

    private string MapToOriginals(string diagnostics)
    {
        var lines = diagnostics.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            foreach (var (relative, original) in _originals)
            {
                var staged = Path.Combine(_sources, relative);
                if (lines[i].StartsWith(staged + "(", StringComparison.Ordinal))
                    lines[i] = original + lines[i][staged.Length..];
                else if (lines[i].StartsWith(relative + "(", StringComparison.Ordinal)
                    || lines[i].StartsWith(relative.Replace('\\', '/') + "(", StringComparison.Ordinal))
                    lines[i] = original + lines[i][relative.Length..];
            }
        return string.Join('\n', lines);
    }

    private static byte[] WithoutCarriageReturns(byte[] bytes)
    {
        var kept = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] != (byte)'\r' || i + 1 >= bytes.Length || bytes[i + 1] != (byte)'\n')
                kept.Add(bytes[i]);
        return kept.ToArray();
    }
}

/// <summary>One slangc compile: the output file, its bytes and the reflection JSON.</summary>
public sealed record SlangOutput(string OutputPath, byte[] Code, string Reflection)
{
    /// <summary>The output as text with LF line endings, for the MSL and HLSL targets.</summary>
    public string Text => Encoding.UTF8.GetString(Code).Replace("\r\n", "\n");
}
