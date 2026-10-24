using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    internal static string Verifier([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,
        "../../../tools/Renderer.Perf.Verify/bin/Release/net10.0/Renderer.Perf.Verify.dll"));
    (int Exit, string Output) RunConsole(params string[] args)
    {
        Assert.True(File.Exists(Verifier()), "Run scripts/build-release-tests.sh KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj before --no-build process tests.");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Verifier());
        foreach (string arg in args) info.ArgumentList.Add(arg);
        info.Environment.Remove("KE_GPU_TESTS");
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        _output.WriteLine("console argv=dotnet {0} {1} cwd={2} exit={3} output={4}", Verifier(), string.Join(" ", args),
            Environment.CurrentDirectory, process.ExitCode, text);
        if (process.ExitCode == 0) Assert.Contains("no GPU providers registered", text, StringComparison.Ordinal);
        return (process.ExitCode, text);
    }
    [Fact]
    public void Console_accepts_exactly_two_arguments_and_bounds_refusal_causes()
    {
        Assert.Equal(2, RunConsole().Exit);
        Assert.Equal(2, RunConsole("one").Exit);
        Assert.Equal(2, RunConsole("one", "two", "three").Exit);
        var result = RunConsole(Path.GetTempPath(), new string('x', 10000));
        Assert.NotEqual(0, result.Exit);
        Assert.True(result.Output.Length <= 600);
    }
}
