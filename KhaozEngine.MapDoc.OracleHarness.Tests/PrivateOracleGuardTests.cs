using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.TileWorld;
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

    [Fact]
    public void Require_RejectsDanglingReportLink() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string report = env[Names[3]]!;
        string target = Path.Combine(root, "out", "absent.json");
        File.CreateSymbolicLink(report, target);
        Assert.Equal("report path already exists",
            Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
        Assert.Equal(PrivateOracleReport.SecureFailure,
            Refuse<PrivateOracleFailure>(() => { using var unused = PrivateOracleReport.Open(report); }).Message);
        Assert.False(File.Exists(target));
    });

    [Fact]
    public void Inventory_CountsOverlayOnlyCellAsVoid() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        var document = new TileWorldDocument { Id = "synthetic-inventory" };
        document.GetOrCreateRegion(new RegionCoord(0, 0));
        document.SetUnderlay(0, 0, 0, 1);
        document.SetUnderlay(1, 0, 0, 1);
        document.SetSettings(1, 0, 0, TileSettings.NoDraw);
        document.SetOverlay(2, 0, 0, 5);
        string world = Path.Combine(root, "world");
        TileWorldFile.Save(document, world);
        PlaneInventory plane = ShippedSourceInventory.Build(world).Planes.Single(p => p.Plane == 0);
        Assert.Equal(1L, plane.DrawableCells);
        Assert.Equal((long)TileRegion.Size * TileRegion.Size - 2, plane.VoidCells);
        Assert.Equal(1L, plane.NoDrawCells);
        Assert.Equal(1L, plane.OverlayCells);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("modified")]
    [InlineData("symlink")]
    public void Require_RefusesChangedSourceWithoutEchoingNames(string change) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string source = env[Names[0]]!;
        string file = Path.Combine(source, "region.txt");
        switch (change)
        {
            case "missing": File.Delete(file); break;
            case "extra": File.WriteAllText(Path.Combine(source, Secret), "extra"); break;
            case "modified": File.AppendAllText(file, Secret); break;
            case "symlink": File.CreateSymbolicLink(Path.Combine(source, Secret), file); break;
        }
        Assert.Equal("source verification failed: 1 files",
            Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
        Assert.False(File.Exists(env[Names[3]]!));
    });

    [Theory]
    [InlineData("aggregate")]
    [InlineData("unsafe-path")]
    [InlineData("duplicate-path")]
    [InlineData("null-tag")]
    [InlineData("missing-tag")]
    public void Require_RefusesInvalidProvenanceWithMatchingOuterDigest(string change) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string file = env[Names[1]]!;
        JsonObject json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        switch (change)
        {
            case "aggregate": json["aggregateSha256"] = new string('0', 64); break;
            case "unsafe-path":
                json["paths"]![0]!["path"] = "../" + Secret;
                RewriteAggregate(json);
                break;
            case "duplicate-path":
                json["paths"]!.AsArray().Add(json["paths"]![0]!.DeepClone());
                RewriteAggregate(json);
                break;
            case "null-tag": json["tag"] = null; break;
            case "missing-tag": json.Remove("tag"); break;
        }
        File.WriteAllText(file, json.ToJsonString());
        env[Names[2]] = AssertFixtures.Sha256(file);
        Assert.Equal("provenance unreadable",
            Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
    });

    [Fact]
    public void Require_AcceptsAnExplicitlyUntaggedImmutableSource() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string file = env[Names[1]]!;
        JsonObject json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["tag"] = "";
        File.WriteAllText(file, json.ToJsonString());
        env[Names[2]] = AssertFixtures.Sha256(file);
        PrivateOracleInputs inputs = PrivateOracleInputs.Require(env);
        Assert.Equal("", inputs.Provenance.Tag);
        Assert.Equal(new string('1', 40), inputs.Provenance.Commit);
    });

    static void RewriteAggregate(JsonObject json)
    {
        ShippedSourceProvenance value = JsonSerializer.Deserialize<ShippedSourceProvenance>(
            json.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
        json["aggregateSha256"] = value.ComputeAggregate();
    }
}
