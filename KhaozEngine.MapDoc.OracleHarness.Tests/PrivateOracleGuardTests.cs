using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

// The harness is Unix-only by D1 because its guard checks POSIX modes, so the scope says so instead of skipping.
[UnsupportedOSPlatform("windows")]
public sealed class PrivateOracleGuardTests
{
    static readonly string[] Names = { "KHAOZ_R2_SHIPPED_SOURCE", "KHAOZ_R2_ORACLE_PROVENANCE", "KHAOZ_R2_ORACLE_PROVENANCE_SHA256", "KHAOZ_R2_ORACLE_REPORT" };
    const string Secret = "SECRET-7f3a";

    static T Refuse<T>(Action act) where T : Exception
    {
        T e = Assert.Throws<T>(act);
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secret, e.ToString());
        return e;
    }

    [Fact]
    public void Require_FailsOnEveryMissingVariableWithoutEchoingValues() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> full = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        foreach (string name in Names)
        {
            var env = new Dictionary<string, string?>(full) { [name] = null };
            Assert.Equal($"missing {name}", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
        }
        full["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = new string('0', 64);
        PrivateOracleInputException mismatch = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(full));
        Assert.Equal("provenance digest mismatch", mismatch.Message);
        Assert.DoesNotContain(root, mismatch.ToString());
    });

    [Theory, InlineData("missing"), InlineData("garbage")]
    public void Require_ProvenanceReadFailuresAreSanitized(string fault) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string provenance = env["KHAOZ_R2_ORACLE_PROVENANCE"]!;
        if (fault == "missing") File.Delete(provenance);
        else { File.WriteAllText(provenance, "{" + Secret); env["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenance); }
        Assert.Equal("provenance unreadable", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
    });

    [Fact]
    public void Require_RefusesAReportPathInsideAGitWorkTreeOrAlreadyPresent() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "repo", "out"));
        env["KHAOZ_R2_ORACLE_REPORT"] = Path.Combine(root, "repo", "out", "r.json");
        Assert.Equal("report path inside a git work tree", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
        string existing = Path.Combine(root, "out", "existing.json");
        File.WriteAllText(existing, "{}");
        env["KHAOZ_R2_ORACLE_REPORT"] = existing;
        Assert.Equal("report path already exists", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
    });

    [Fact]
    public void FromEnvironment_ReadsOnlyTheFourNamedVariables()
    {
        var asked = new List<string>();
        Assert.Equal("missing KHAOZ_R2_SHIPPED_SOURCE",
            Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.FromEnvironment(n => { asked.Add(n); return null; })).Message);
        Assert.Equal(Names, asked);
    }

    [Fact]
    public void Report_FailureMessageCarriesOnlyCategoryCountAndDigest() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        string path = Path.Combine(root, "out", "r.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using PrivateOracleReport report = PrivateOracleReport.Open(path);
        report.Check("vertex", false, "{\"x\":12.5}");
        report.Check("vertex", false, "{\"x\":12.5}");
        PrivateOracleFailure e = Assert.Throws<PrivateOracleFailure>(report.ThrowIfAnyFailed);
        Assert.Matches("^private oracle: 2 vertex mismatches, report sha256 [0-9a-f]{64}$", e.Message);
        Assert.DoesNotContain("12.5", e.ToString());
        Assert.Contains("12.5", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    });

    [Theory, InlineData("filesystem"), InlineData("json"), InlineData("loader"), InlineData("arithmetic")]
    public void Entry_SanitizesEveryFailureRoute(string fault) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        PrivateOracleFailure e = Refuse<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, (inputs, _) => PrivateOracleFixtures.Throw(fault, inputs.SourceRoot)));
        Assert.Matches("^private oracle: exception failure, report sha256 [0-9a-f]{64}$", e.Message);
        Assert.Contains(Secret, File.ReadAllText(env["KHAOZ_R2_ORACLE_REPORT"]!));
    });

    [Fact]
    public void Entry_FailsClosedWhenTheReportCannotBeCreatedSecurely() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        File.SetUnixFileMode(Path.Combine(root, "out"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        bool ran = false;
        Assert.Equal("private oracle: report could not be created securely",
            Refuse<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, (_, _) => ran = true)).Message);
        Assert.False(ran);
        Assert.False(File.Exists(env["KHAOZ_R2_ORACLE_REPORT"]!));
    });
}
