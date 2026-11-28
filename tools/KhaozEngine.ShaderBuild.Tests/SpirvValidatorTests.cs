using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class SpirvValidatorTests : IDisposable
{
    private static readonly string ValidatorName = OperatingSystem.IsWindows() ? "spirv-val.exe" : "spirv-val";

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "khaozengine-spirv-validator-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configured;
    private readonly string _onPath;

    public SpirvValidatorTests()
    {
        _configured = Touch(Path.Combine(_folder, "configured", ValidatorName));
        _onPath = Touch(Path.Combine(_folder, "on-path", ValidatorName));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("configured beats path", "configured")]
    [InlineData("path only", "on-path")]
    [InlineData("nothing", null)]
    public void Finds_the_configured_validator_then_one_on_path(string scenario, string? expectedFolder)
    {
        var found = SpirvValidator.Find(EnvironmentFor(scenario));

        Assert.Equal(expectedFolder is null ? null : Path.Combine(_folder, expectedFolder, ValidatorName), found);
    }

    [Theory]
    [InlineData("configured missing")]
    [InlineData("nothing but required")]
    public void Refuses_a_missing_validator(string scenario)
    {
        var refusal = Assert.Throws<ShaderBuildException>(() => SpirvValidator.Find(EnvironmentFor(scenario)));

        Assert.Equal(ShaderBuildFailure.ToolchainMissing, refusal.Failure);
    }

    [Fact]
    public void Refuses_spirv_the_validator_rejects_and_accepts_what_it_passes()
    {
        SpirvValidator.Validate(Stub("passing", exitCode: 0), "module.spv");

        var refusal = Assert.Throws<ShaderBuildException>(() => SpirvValidator.Validate(Stub("rejecting", exitCode: 3), "module.spv"));

        Assert.Equal(ShaderBuildFailure.CompileFailed, refusal.Failure);
    }

    private Func<string, string?> EnvironmentFor(string scenario)
    {
        var empty = Path.Combine(_folder, "empty");
        Directory.CreateDirectory(empty);
        var variables = scenario switch
        {
            "configured beats path" => new Dictionary<string, string> { ["SPIRV_VAL"] = _configured, ["PATH"] = Path.GetDirectoryName(_onPath)! },
            "path only" => new Dictionary<string, string> { ["PATH"] = empty + Path.PathSeparator + Path.GetDirectoryName(_onPath) },
            "nothing" => new Dictionary<string, string> { ["PATH"] = empty },
            "configured missing" => new Dictionary<string, string> { ["SPIRV_VAL"] = Path.Combine(empty, ValidatorName), ["PATH"] = Path.GetDirectoryName(_onPath)! },
            "nothing but required" => new Dictionary<string, string> { ["PATH"] = empty, ["KHAOZ_REQUIRE_SPIRV_VAL"] = "1" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        return name => variables.GetValueOrDefault(name);
    }

    private string Stub(string name, int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(_folder, name + ".cmd");
            File.WriteAllText(script, $"@exit /b {exitCode}\r\n");
            return script;
        }
        var shell = Path.Combine(_folder, name);
        File.WriteAllText(shell, $"#!/bin/sh\nexit {exitCode}\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shell;
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }
}
