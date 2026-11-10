using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageVersionProcessTests
{
    // #1765. Installed verifier provenance must name both its engine and arithmetic contract.
    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    public async Task Version_reports_the_built_engine_and_carry_contract(string command)
    {
        var start = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(command);
        start.Environment["KE_GPU_TESTS"] = "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await error);
        using var json = JsonDocument.Parse(await output);
        Assert.Equal(typeof(CoverageRecessionTable).Assembly.GetName().Version!.ToString(3), json.RootElement.GetProperty("engineVersion").GetString());
        Assert.Equal(CoverageArithmeticContract.Version, json.RootElement.GetProperty("carryArithmeticContractVersion").GetInt32());
        Assert.Equal(2, Program.Main([command, "unexpected"]));
    }
}
