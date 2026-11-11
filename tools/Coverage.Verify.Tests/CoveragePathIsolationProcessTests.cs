using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePathIsolationProcessTests
{
    // #1766: diagnostic publication must not overwrite inputs or accepted artifacts through directory links.
    [DirectoryLinkTheory]
    [InlineData("manifest.json", false)]
    [InlineData("manifest.json", true)]
    [InlineData("table.bin", false)]
    [InlineData("table.bin", true)]
    [InlineData("evidence.json", false)]
    [InlineData("evidence.json", true)]
    [InlineData("host.glb", false)]
    [InlineData("host.glb", true)]
    public async Task Linked_diagnostic_alias_refuses_before_accepted_or_refused_publication(string name, bool refused)
    {
        using var files = new CoverageBuildFiles("path-isolation");
        Assert.Equal(0, (await Execute(files)).ExitCode);
        if (refused)
        {
            files.Manifest["samples"]![1]!["stateKey"] = 1;
            files.Write();
            Assert.Equal(1, (await Execute(files)).ExitCode);
        }
        string diagnostic = Path.Combine(files.Folder, "diagnostics.json");
        File.WriteAllText(diagnostic, "diagnostic-sentinel");
        var before = Snapshot(files);
        string link = Path.Combine(files.Folder, "linked");
        Directory.CreateSymbolicLink(link, files.Folder);
        try
        {
            Assert.Equal(2, (await Execute(files, diagnostic: Path.Combine(link, name))).ExitCode);
            AssertPreserved(files, before);
        }
        finally { Directory.Delete(link); }
    }

    [DirectoryLinkTheory]
    [InlineData("table.bin")]
    [InlineData("evidence.json")]
    public async Task Linked_diagnostic_alias_is_rejected_even_before_destination_exists(string name)
    {
        using var files = new CoverageBuildFiles("path-isolation");
        var before = Snapshot(files);
        string link = Path.Combine(files.Folder, "linked");
        Directory.CreateSymbolicLink(link, files.Folder);
        try
        {
            Assert.Equal(2, (await Execute(files, diagnostic: Path.Combine(link, name))).ExitCode);
            AssertPreserved(files, before);
            Assert.False(File.Exists(files.TablePath));
            Assert.False(File.Exists(files.EvidencePath));
        }
        finally { Directory.Delete(link); }
    }

    // No path comparison can enumerate every spelling of one directory, so the diagnostic file name itself
    // must be unique within the run. A real, unlinked directory proves the rule does not depend on resolution.
    [Theory]
    [InlineData("manifest.json")]
    [InlineData("TABLE.BIN")]
    [InlineData("Evidence.json")]
    [InlineData("host.glb")]
    public async Task Diagnostic_file_name_shared_with_a_run_file_is_refused_in_any_directory(string name)
    {
        using var files = new CoverageBuildFiles("path-isolation");
        Assert.Equal(0, (await Execute(files)).ExitCode);
        var before = Snapshot(files);
        string elsewhere = Path.Combine(files.Folder, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.Equal(2, (await Execute(files, diagnostic: Path.Combine(elsewhere, name))).ExitCode);
        AssertPreserved(files, before);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    // A parent link whose target ends in a dot segment resolves differently on disk than lexically. The
    // diagnostic must still be refused, and two physically distinct accepted outputs must still publish.
    [DirectoryLinkTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nested_parent_link_neither_hides_a_diagnostic_alias_nor_refuses_distinct_outputs(bool diagnosticAlias)
    {
        using var files = new CoverageBuildFiles("path-isolation");
        string real = Path.Combine(files.Folder, "real"), hop = Path.Combine(files.Folder, "hop"), alias = Path.Combine(files.Folder, "alias");
        Directory.CreateDirectory(Path.Combine(real, "inner"));
        Directory.CreateSymbolicLink(hop, Path.Combine("real", "inner"));
        Directory.CreateSymbolicLink(alias, Path.Combine("hop", ".."));
        try
        {
            // On disk alias names real, so alias/table.bin is real/table.bin and never the fixture's table.bin.
            string physical = Path.Combine(real, "table.bin");
            if (diagnosticAlias)
            {
                File.WriteAllText(physical, "table-sentinel");
                var result = await Execute(files, diagnostic: Path.Combine(alias, "table.bin"), table: physical);
                Assert.Equal(2, result.ExitCode);
                Assert.Equal("table-sentinel", File.ReadAllText(physical));
                Assert.False(File.Exists(files.EvidencePath));
            }
            else
            {
                var result = await Execute(files, evidence: Path.Combine(alias, "table.bin"));
                Assert.Equal(0, result.ExitCode);
                Assert.Equal((byte)'{', File.ReadAllBytes(physical)[0]);
                Assert.NotEqual(File.ReadAllBytes(physical), File.ReadAllBytes(files.TablePath));
            }
        }
        finally { Directory.Delete(alias); Directory.Delete(hop); }
    }

    // The accepted-output checks still compare spellings, exactly as they did before diagnostics existed.
    [Theory]
    [InlineData("table", "manifest.json")]
    [InlineData("evidence", "manifest.json")]
    [InlineData("table", "host.glb")]
    [InlineData("evidence", "host.glb")]
    [InlineData("evidence", "table.bin")]
    public async Task Accepted_output_naming_another_run_file_keeps_the_output_alias_refusal(string output, string name)
    {
        using var files = new CoverageBuildFiles("path-isolation");
        Assert.Equal(0, (await Execute(files)).ExitCode);
        var before = Snapshot(files);
        string alias = Path.Combine(files.Folder, name);

        var result = await Execute(files, table: output == "table" ? alias : null, evidence: output == "evidence" ? alias : null);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(nameof(CoverageBuildValidationRefusal.OutputAlias), result.Error.Trim());
        AssertPreserved(files, before);
    }

    static Dictionary<string, byte[]> Snapshot(CoverageBuildFiles files) => Directory.GetFiles(files.Folder)
        .ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);

    static void AssertPreserved(CoverageBuildFiles files, Dictionary<string, byte[]> before)
    {
        Assert.NotEmpty(before);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(files.Folder).Select(Path.GetFileName).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(files.Folder, file.Key)));
    }

    static async Task<(int ExitCode, string Error)> Execute(CoverageBuildFiles files, string? diagnostic = null,
        string? table = null, string? evidence = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { typeof(Program).Assembly.Location, "build", "--manifest", files.ManifestPath,
            "--table", table ?? files.TablePath, "--evidence", evidence ?? files.EvidencePath }) start.ArgumentList.Add(arg);
        if (diagnostic != null) { start.ArgumentList.Add("--diagnostics"); start.ArgumentList.Add(diagnostic); }
        start.Environment["KE_GPU_TESTS"] = "0";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        return (process.ExitCode, await stderr);
    }
}

/// <summary>Reports Windows link-creation privilege failures as explicit skips, like the repository's provider gates.</summary>
public sealed class DirectoryLinkTheoryAttribute : TheoryAttribute
{
    static readonly Lazy<string?> UnavailableReason = new(Probe);
    public DirectoryLinkTheoryAttribute() => Skip = UnavailableReason.Value;

    static string? Probe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string folder = Path.Combine(Path.GetTempPath(), "khaozengine-link-probe-" + Guid.NewGuid().ToString("N"));
        string link = Path.Combine(folder, "link");
        Directory.CreateDirectory(folder);
        try { Directory.CreateSymbolicLink(link, folder); return null; }
        catch (UnauthorizedAccessException) { return "Directory symbolic links require Windows Developer Mode or link-creation privilege."; }
        catch (IOException error) when ((error.HResult & 0xffff) == 1314)
        { return "Windows denied symbolic-link creation because the required privilege is not held."; }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(folder);
        }
    }
}
