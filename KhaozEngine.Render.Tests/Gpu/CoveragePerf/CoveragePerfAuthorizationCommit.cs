using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

// The checkout is authorization commit B. Its only parent is the measured workload commit A, and it adds
// exactly the approved manifest. Hosted checkouts fetch depth 2 so A is present, and nothing here fetches.
internal static class CoveragePerfAuthorizationCommit
{
    internal static string ManifestPath(string unit) => "tools/EquipmentCoverage.Perf.Tests/authorized/" + unit + ".json";

    internal static string Commit(string root, string workloadSha, string unit, string gitExecutable = "git")
    {
        string head = RunGit(root, gitExecutable, "rev-parse", "--verify", "HEAD").Trim();
        if (!Regex.IsMatch(head, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("HEAD does not resolve to a full commit SHA.");
        if (head == workloadSha)
            throw new InvalidDataException("HEAD is the workload commit. Dispatch must check out its authorization commit.");
        string[] commits = RunGit(root, gitExecutable, "rev-list", "--parents", "-n", "1", "HEAD").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length == 0 || commits[0] != head) throw new InvalidDataException("Cannot read the authorization commit's parents.");
        if (commits.Length != 2)
            throw new InvalidDataException($"Authorization commit must have exactly one parent, found {commits.Length - 1}.");
        if (commits[1] != workloadSha)
            throw new InvalidDataException("Authorization commit's parent differs from KE_COVERAGE_PERF_WORKLOAD_SHA.");
        // No rename pairing, so a renamed or deleted source file is reported as its own change.
        string[] changes = RunGit(root, gitExecutable, "diff", "--name-status", "--no-renames", workloadSha, head)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (changes.Length != 1 || changes[0] != "A\t" + ManifestPath(unit))
            throw new InvalidDataException($"Authorization commit must add only {ManifestPath(unit)}, found: {string.Join(", ", changes)}.");
        return head;
    }

    // Arguments are passed as a list, never through a shell. User, system and GIT_* environment settings are
    // excluded so neither aliases, rename settings nor a redirected repository can change the answer.
    internal static string Git(string root, params string[] arguments) => RunGit(root, "git", arguments);

    static string RunGit(string root, string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception error) { throw new InvalidDataException("git is unavailable, so the authorization commit cannot be verified: " + error.Message, error); }
        using (process ?? throw new InvalidDataException("git is unavailable, so the authorization commit cannot be verified."))
        {
            var error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidDataException("git " + arguments.FirstOrDefault() + " failed: " + error.GetAwaiter().GetResult().Trim());
            return output;
        }
    }
}
