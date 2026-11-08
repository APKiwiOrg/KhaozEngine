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
            PrivateOracleInputException missing = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
            Assert.Equal(PrivateOracleInputRefusal.MissingInput, missing.Reason);
            Assert.Equal(name, missing.InputName);
            Assert.Null(missing.MismatchCount);
            Assert.False(File.Exists(full[Names[3]]!));
        }
        PrivateOracleInputs.Require(full);
        string digest = full[Names[2]]!;
        full["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = new string('0', 64);
        PrivateOracleInputException mismatch = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(full));
        Assert.Equal(PrivateOracleInputRefusal.ProvenanceDigestMismatch, mismatch.Reason);
        Assert.Null(mismatch.InputName);
        Assert.Null(mismatch.MismatchCount);
        Assert.DoesNotContain(root, mismatch.ToString());
        Assert.False(File.Exists(full[Names[3]]!));
        full[Names[2]] = digest;
        PrivateOracleInputs.Require(full);
    });

    [Theory, InlineData("missing"), InlineData("garbage")]
    public void Require_ProvenanceReadFailuresAreSanitized(string fault) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string provenance = env["KHAOZ_R2_ORACLE_PROVENANCE"]!;
        byte[] original = File.ReadAllBytes(provenance);
        if (fault == "missing") File.Delete(provenance);
        else { File.WriteAllText(provenance, "{" + Secret); env["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenance); }
        PrivateOracleInputException failure = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
        Assert.Equal(PrivateOracleInputRefusal.ProvenanceUnreadable, failure.Reason);
        Assert.Null(failure.InputName);
        Assert.Null(failure.MismatchCount);
        Assert.False(File.Exists(env[Names[3]]!));
        File.WriteAllBytes(provenance, original);
        env[Names[2]] = AssertFixtures.Sha256(provenance);
        PrivateOracleInputs.Require(env);
    });

    [Fact]
    public void Require_RefusesAReportPathInsideAGitWorkTreeOrAlreadyPresent() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "repo", "out"));
        env["KHAOZ_R2_ORACLE_REPORT"] = Path.Combine(root, "repo", "out", "r.json");
        PrivateOracleInputException inside = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
        Assert.Equal(PrivateOracleInputRefusal.ReportInsideWorktree, inside.Reason);
        Assert.False(File.Exists(env[Names[3]]!));
        Directory.Delete(Path.Combine(root, "repo", ".git"));
        PrivateOracleInputs.Require(env);
        string existing = Path.Combine(root, "out", "existing.json");
        File.WriteAllText(existing, "{}");
        env["KHAOZ_R2_ORACLE_REPORT"] = existing;
        PrivateOracleInputException present = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
        Assert.Equal(PrivateOracleInputRefusal.ReportAlreadyExists, present.Reason);
        Assert.Equal("{}", File.ReadAllText(existing));
        File.Delete(existing);
        PrivateOracleInputs.Require(env);
    });

    [Fact]
    public void FromEnvironment_ReadsOnlyTheFourNamedVariables() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        var asked = new List<string>();
        PrivateOracleInputException failure = Refuse<PrivateOracleInputException>(() =>
            PrivateOracleInputs.FromEnvironment(n => { asked.Add(n); return null; }));
        Assert.Equal(PrivateOracleInputRefusal.MissingInput, failure.Reason);
        Assert.Equal(Names[0], failure.InputName);
        Assert.Equal(Names, asked);
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        PrivateOracleInputException unreadable = Refuse<PrivateOracleInputException>(() =>
            PrivateOracleInputs.FromEnvironment(_ => throw new IOException(Secret)));
        Assert.Equal(PrivateOracleInputRefusal.ProvenanceUnreadable, unreadable.Reason);
        Assert.False(File.Exists(env[Names[3]]!));
        PrivateOracleInputs.FromEnvironment(n => env[n]);
    });

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
        Assert.Equal(PrivateOracleFailureRefusal.ComparisonMismatches, e.Reason);
        Assert.Equal("vertex", e.Category);
        Assert.Equal(2, e.MismatchCount);
        Assert.Equal(AssertFixtures.Sha256(path), e.ReportSha256);
        Assert.Null(e.InnerException);
        Assert.DoesNotContain("12.5", e.ToString());
        Assert.Contains("12.5", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        using PrivateOracleReport control = PrivateOracleReport.Open(Path.Combine(root, "out", "passing.json"));
        control.Check("vertex", true, "{\"x\":12.5}");
        control.ThrowIfAnyFailed();
    });

    [Theory, InlineData("filesystem"), InlineData("json"), InlineData("loader"), InlineData("arithmetic")]
    public void Entry_SanitizesEveryFailureRoute(string fault) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        PrivateOracleFailure e = Refuse<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, (inputs, _) => PrivateOracleFixtures.Throw(fault, inputs.SourceRoot)));
        Assert.Equal(PrivateOracleFailureRefusal.ExecutionFailed, e.Reason);
        Assert.Null(e.Category);
        Assert.Null(e.MismatchCount);
        Assert.Equal(AssertFixtures.Sha256(env[Names[3]]!), e.ReportSha256);
        Assert.Contains(Secret, File.ReadAllText(env["KHAOZ_R2_ORACLE_REPORT"]!));
        File.Delete(env[Names[3]]!);
        PrivateOracleEntry.Run(env, (_, _) => { });
    });

    [Fact]
    public void Entry_FailsClosedWhenTheReportCannotBeCreatedSecurely() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        File.SetUnixFileMode(Path.Combine(root, "out"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        bool ran = false;
        PrivateOracleFailure failure = Refuse<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, (_, _) => ran = true));
        Assert.Equal(PrivateOracleFailureRefusal.SecureReportUnavailable, failure.Reason);
        Assert.Null(failure.Category);
        Assert.Null(failure.MismatchCount);
        Assert.Null(failure.ReportSha256);
        Assert.False(ran);
        Assert.False(File.Exists(env["KHAOZ_R2_ORACLE_REPORT"]!));
        File.SetUnixFileMode(Path.Combine(root, "out"), PrivateOracleFixtures.SecureDirectoryMode);
        PrivateOracleEntry.Run(env, (_, _) => ran = true);
        Assert.True(ran);
    });

    [Fact]
    public void Require_RejectsDanglingReportLink() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string report = env[Names[3]]!;
        string target = Path.Combine(root, "out", "absent.json");
        File.CreateSymbolicLink(report, target);
        PrivateOracleInputException present = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
        Assert.Equal(PrivateOracleInputRefusal.ReportAlreadyExists, present.Reason);
        PrivateOracleFailure unsafeReport = Refuse<PrivateOracleFailure>(() => { using var unused = PrivateOracleReport.Open(report); });
        Assert.Equal(PrivateOracleFailureRefusal.SecureReportUnavailable, unsafeReport.Reason);
        Assert.Null(unsafeReport.ReportSha256);
        Assert.False(File.Exists(target));
        File.Delete(report);
        PrivateOracleEntry.Run(env, (_, _) => { });
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
    [InlineData("missing", 1)]
    [InlineData("extra", 1)]
    [InlineData("modified", 1)]
    [InlineData("symlink", 1)]
    [InlineData("modified-and-extra", 2)]
    [InlineData("invalid-root", 1)]
    public void Require_RefusesChangedSourceWithoutEchoingNames(string change, int mismatches) => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
        string source = env[Names[0]]!;
        string file = Path.Combine(source, "region.txt");
        byte[] original = File.ReadAllBytes(file);
        switch (change)
        {
            case "missing": File.Delete(file); break;
            case "extra": File.WriteAllText(Path.Combine(source, Secret), "extra"); break;
            case "modified": File.AppendAllText(file, Secret); break;
            case "symlink": File.CreateSymbolicLink(Path.Combine(source, Secret), file); break;
            case "modified-and-extra":
                File.AppendAllText(file, Secret);
                File.WriteAllText(Path.Combine(source, Secret), "extra");
                break;
            case "invalid-root": env[Names[0]] = source + '\0' + Secret; break;
        }
        PrivateOracleInputException failure = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
        Assert.Equal(PrivateOracleInputRefusal.SourceVerificationFailed, failure.Reason);
        Assert.Equal(mismatches, failure.MismatchCount);
        Assert.Null(failure.InputName);
        Assert.False(File.Exists(env[Names[3]]!));
        if (change is "extra" or "symlink" or "modified-and-extra") File.Delete(Path.Combine(source, Secret));
        if (change is "missing" or "modified" or "modified-and-extra") File.WriteAllBytes(file, original);
        env[Names[0]] = source;
        PrivateOracleInputs.Require(env);
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
        byte[] original = File.ReadAllBytes(file);
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
        if (change != "missing-tag")
        {
            var data = Refuse<PrivateOracleDataException>(() => ShippedSourceProvenance.Parse(File.ReadAllBytes(file)));
            Assert.Equal(change switch
            {
                "aggregate" => PrivateOracleDataRefusal.AttestedDigestMismatch,
                "unsafe-path" => PrivateOracleDataRefusal.PathUnsafe,
                "duplicate-path" => PrivateOracleDataRefusal.IdentityNotUniqueOrCanonical,
                _ => PrivateOracleDataRefusal.ValueRequired,
            }, data.Reason);
        }
        PrivateOracleInputException failure = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env));
        Assert.Equal(PrivateOracleInputRefusal.ProvenanceUnreadable, failure.Reason);
        Assert.False(File.Exists(env[Names[3]]!));
        File.WriteAllBytes(file, original);
        env[Names[2]] = AssertFixtures.Sha256(file);
        PrivateOracleInputs.Require(env);
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

    [Fact]
    public void DataRefusal_DoesNotRetainAPrivateOriginalException()
    {
        var error = new PrivateOracleDataException(PrivateOracleDataRefusal.SchemaShapeInvalid, "invalid provenance", new IOException(Secret));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    static void RewriteAggregate(JsonObject json)
    {
        ShippedSourceProvenance value = JsonSerializer.Deserialize<ShippedSourceProvenance>(
            json.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
        json["aggregateSha256"] = value.ComputeAggregate();
    }
}
