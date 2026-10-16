using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportCostArtifactTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ArtifactAppliesTheMeasuredExplicitLifetimeInventory(int extraBytes, bool expected)
    {
        var tokens = ContactLifetimeAllocationTests.Inventory();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SupportCostSample candidate = Sample() with
            {
                Operation = "ExplicitTick",
                GeometryPassed = true,
                Refusal = false,
                Height = 0,
                HeightError = 0,
                AchievedMotion = Vector3.UnitX,
                Substeps = 1,
                WarmMicroseconds = 50,
                WarmBytes = tokens.Movement + tokens.Physics + tokens.Pin + extraBytes
            };
            SupportCostSample legacy = candidate with { Operation = "LegacyTick", WarmMicroseconds = 20, WarmBytes = 64 };
            SupportCostArtifact.Write(path, [candidate, legacy], "base", "candidate", "command");
            JsonNode gate = JsonNode.Parse(File.ReadAllText(path))!["baseline"]!["Samples"]![0]!["GatePassed"]!;
            Assert.Equal(expected, gate.GetValue<bool>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArtifactRetainsBaselineWhenChangedSamplesAreFinalized()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cost.json");
        try
        {
            SupportCostArtifact.Write(path, [Sample()], "base", "candidate", "command");
            Assert.Equal(0, Finalize(path, 1, true));
            JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string baseline = root["baseline"]!.ToJsonString();
            Assert.Equal(1, root["baseline"]!["ProcessExitStatus"]!.GetValue<int>());
            SupportCostArtifact.Write(path, [Sample()], "base", "changed", "command");
            Assert.Equal(0, Finalize(path, 0, true));
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(baseline, root["baseline"]!.ToJsonString());
            Assert.Equal(0, root["changed"]!["ProcessExitStatus"]!.GetValue<int>());
            Assert.Equal("harness-corrected", root["changed"]!["RecordingLabel"]?.GetValue<string>());
            Assert.Equal(1, root["changed"]!["UnmeasuredPrimingCallsPerRow"]?.GetValue<int>());
            JsonNode inventory = root["changed"]!["LifetimeInventory"]!;
            Assert.NotNull(inventory);
            long movement = inventory["MovementQueryLeaseAndReleaseBindingBytes"]!.GetValue<long>();
            long physics = inventory["PhysicsReadLeaseBytes"]!.GetValue<long>();
            long pin = inventory["ProducerPinBytes"]!.GetValue<long>();
            Assert.True(movement > 0 && physics > 0 && pin > 0);
            Assert.Equal(movement + physics + pin, inventory["AcquiredExplicitTickBytes"]!.GetValue<long>());
            Assert.Null(root["pendingFinalization"]);
            Assert.NotEqual(0, Finalize(path, 0));
            string firstChanged = root["changed"]!.ToJsonString();
            SupportCostArtifact.Write(path, [Sample()], "base", "third", "command");
            Assert.Equal(0, Finalize(path, 0));
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(baseline, root["baseline"]!.ToJsonString());
            Assert.Equal(firstChanged, root["changed"]!.ToJsonString());
            Assert.Equal("third", root["changed-third"]!["CandidateSha"]!.GetValue<string>());
            Assert.Equal(0, root["changed-third"]!["ProcessExitStatus"]!.GetValue<int>());
            Assert.Throws<InvalidOperationException>(() => SupportCostArtifact.Write(path, [Sample()], "base", "third", "command"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ArtifactPreservesInputAndFirstUseAndWarmStates()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cost.json");
        try
        {
            var row = new SupportCostRow("fixture") { Feet = new(1, 2, 3) };
            var sample = Sample() with
            {
                Input = row,
                EndState = new MoveState { Position = Vector3.One },
                ColdEndState = new MoveState { Position = new(2, 3, 4) },
                ColdHeight = 2,
                Height = 1
            };
            SupportCostArtifact.Write(path, [sample, sample with { Operation = "DryTick", Height = 3 }], "base", "candidate", "command");
            Assert.Equal(0, Finalize(path, 0, true));
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(2, root["schemaVersion"]!.GetValue<int>());
            JsonNode section = root["baseline"]!;
            JsonNode input = section["Scenes"]!["fixture"]!;
            Assert.Single(section["Scenes"]!.AsObject());
            Assert.Equal(1, input["InputState"]!["Position"]!["X"]!.GetValue<float>());
            Assert.Equal(2.9f, input["InputState"]!["Position"]!["Y"]!.GetValue<float>());
            JsonNode record = section["Samples"]![0]!;
            Assert.Null(record["Input"]);
            Assert.Equal("fixture", record["Scene"]!.GetValue<string>());
            Assert.Equal("fixture", section["Samples"]![1]!["Scene"]!.GetValue<string>());
            Assert.Equal(3, section["Samples"]![1]!["Height"]!.GetValue<double>());
            Assert.Equal(2, section["Samples"]!.AsArray().Count);
            Assert.Equal(16, record["ColdBytes"]!.GetValue<long>());
            Assert.Equal(0, record["WarmBytes"]!.GetValue<long>());
            Assert.Equal(1, record["ColdMicroseconds"]!.GetValue<double>());
            Assert.Equal(2, record["WarmMicroseconds"]!.GetValue<double>());
            Assert.Equal(1, record["EndState"]!["Position"]!["X"]!.GetValue<float>());
            Assert.Equal(4, record["ColdEndState"]!["Position"]!["Z"]!.GetValue<float>());
            Assert.Equal(2, record["ColdHeight"]!.GetValue<double>());
            Assert.Equal(1, record["Height"]!.GetValue<double>());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ArtifactRejectsConflictingSceneInputsWithoutWriting()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        SupportCostSample first = Sample() with { Input = new("fixture") { Feet = Vector3.Zero } };
        SupportCostSample second = Sample() with { Input = new("fixture") { Feet = Vector3.One } };
        try
        {
            Assert.Throws<ArgumentException>(() => SupportCostArtifact.Write(path, [first, second], "base", "candidate", "command"));
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("mismatch")]
    [InlineData("empty")]
    public void FinalizerRefusesInvalidSamplesWithoutChangingTheArtifact(string fault)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cost.json");
        try
        {
            if (fault != "missing")
            {
                SupportCostArtifact.Write(path, [Sample()], "base", "candidate", "command");
                JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                if (fault == "stale") root["baseline"]!["CapturedAtUtc"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
                if (fault == "mismatch") root["pendingFinalization"]!["RunId"] = "wrong";
                if (fault == "empty") root["baseline"]!["Samples"] = new JsonArray();
                File.WriteAllText(path, root.ToJsonString());
            }
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            Assert.NotEqual(0, Finalize(path, 0));
            Assert.Equal(before, File.Exists(path) ? File.ReadAllText(path) : null);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void FinalizerAcceptsDotnetIsoFractionPrecision(int digits)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cost.json");
        try
        {
            SupportCostArtifact.Write(path, [Sample()], "base", "candidate", "command");
            JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string format = "yyyy-MM-dd'T'HH:mm:ss." + new string('f', digits) + "zzz";
            root["baseline"]!["CapturedAtUtc"] = DateTimeOffset.UtcNow.ToString(format,
                System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(path, root.ToJsonString());
            Assert.Equal(0, Finalize(path, 0, true));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Exit75DoesNotRequireOrAlterSamples()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        Assert.Equal(0, Finalize(path, 75, true));
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "untouched");
        try
        {
            Assert.Equal(0, Finalize(path, 75, true));
            Assert.Equal("untouched", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    static SupportCostSample Sample() => new("Find", "fixture", false, "Refused", double.NaN,
        double.NaN, Vector3.Zero, 0, default, 16, 0, 1, 2, 128, 4352)
    { Refusal = true };

    const string HostedSha = "376971dda8964849cac1a13fa7cad17a26bd45a5";
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    [Theory]
    [InlineData(200, 25, true)]
    [InlineData(200, 26, false)]
    [InlineData(800, 100, true)]
    [InlineData(800, 101, false)]
    [InlineData(0, 1, false)]
    public void HostedArtifactUsesReadOnlyCalibration(double hostedTime, double findTime, bool expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string referencePath = Path.Combine(directory, "reference.json");
        string path = Path.Combine(directory, "support-cost", "run-1.json");
        File.WriteAllText(referencePath, ReferenceRoot(HostedSha).ToJsonString());
        string before = File.ReadAllText(referencePath);
        SupportCostSample[] samples = HostedSamples(hostedTime);
        SupportCostSample candidate = samples[0] with { Operation = "Find", WarmMicroseconds = findTime };
        string? tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", "0");
            SupportCostArtifact.Write(path, [candidate, .. samples, samples[0] with { Operation = "LegacyDownPass" }],
                "base", HostedSha, "command", referencePath);
            JsonNode section = JsonNode.Parse(File.ReadAllText(path))!["baseline"]!;
            if (hostedTime > 0) Assert.True(section["HostedNormalizationPassed"]!.GetValue<bool>(),
                "The matching hosted record must calibrate.");
            Assert.Equal(expected, section["Samples"]![0]!["GatePassed"]!.GetValue<bool>());
            if (hostedTime > 0)
            {
                Assert.Equal(hostedTime / 400, section["HostScale"]!.GetValue<double>());
                Assert.Equal(SupportGateCostTests.HostedRows, section["HostScaleRows"]!.GetValue<int>());
                Assert.Equal("reference-376971dda", section["ReferenceSection"]!.GetValue<string>());
            }
            else Assert.Null(section["HostScale"]);
            Assert.Equal(before, File.ReadAllText(referencePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", tiered);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("pending")]
    [InlineData("schema")]
    [InlineData("baseline only")]
    [InlineData("other commit")]
    [InlineData("mislabeled commit")]
    public void HostedArtifactCannotPassWithoutFinalizedReference(string fault)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string referencePath = Path.Combine(directory, "reference.json"), path = Path.Combine(directory, "run-1.json");
        SupportCostSample[] samples = HostedSamples(200);
        string? tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", "0");
            if (fault == "malformed") File.WriteAllText(referencePath, "{");
            if (fault != "missing" && fault != "malformed")
            {
                JsonObject root = ReferenceRoot(fault == "other commit" ? "06b734c5722bc2cc95cea459255d6ec93e9236c7" : HostedSha);
                if (fault == "schema") root["schemaVersion"] = 1;
                if (fault == "pending") root["pendingFinalization"] = new JsonObject();
                if (fault == "baseline only")
                {
                    root["baseline"] = root["reference-376971dda"]!.DeepClone();
                    root.Remove("reference-376971dda");
                }
                if (fault == "mislabeled commit") root["reference-376971dda"]!["CandidateSha"] = "376971ddb8964849cac1a13fa7cad17a26bd45a5";
                File.WriteAllText(referencePath, root.ToJsonString());
            }
            SupportCostArtifact.Write(path, [samples[0] with { Operation = "Find", WarmMicroseconds = 1 }, .. samples,
                samples[0] with { Operation = "LegacyDownPass" }], "base", HostedSha, "command", referencePath);
            JsonNode section = JsonNode.Parse(File.ReadAllText(path))!["baseline"]!;
            Assert.False(section["HostedNormalizationPassed"]!.GetValue<bool>());
            Assert.False(section["Samples"]![0]!["GatePassed"]!.GetValue<bool>());
            Assert.Null(section["HostScale"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredCompilation", tiered);
            Directory.Delete(directory, true);
        }
    }

    static JsonObject ReferenceRoot(string commit)
    {
        JsonObject reference = SupportGateCostTests.HostedSection(400, true);
        reference["Tuning"] = JsonSerializer.SerializeToNode(SupportGateCostScenes.Tuning, Options);
        reference["CandidateSha"] = commit;
        return new JsonObject { ["schemaVersion"] = 2, ["reference-" + commit[..9]] = reference };
    }

    static SupportCostSample[] HostedSamples(double time) =>
        SupportGateCostTests.HostedSection(time)["Samples"]!.AsArray().Select(sample =>
        {
            SupportCostSample legacy = sample.Deserialize<SupportCostSample>(Options)!;
            return legacy with { Input = new(legacy.Scene) };
        }).ToArray();

    [Fact]
    public void HostedScaleIsTheMedianOverComparableLegacyRows()
    {
        JsonObject reference = SupportGateCostTests.HostedSection(10, true, 41), hosted = SupportGateCostTests.HostedSection(20, false, 41);
        hosted["Samples"]![0]!["WarmMicroseconds"] = 6;
        Assert.Equal("flat box", hosted["Samples"]![0]!["Scene"]!.GetValue<string>());
        Assert.True(SupportCostVerdict.TryHostedScale(reference, hosted, out HostedScale scale));
        Assert.Equal(new HostedScale(2, 41, 2, 2), scale);
    }

    [Fact]
    public void HostedScaleRefusesBelowFortyRows()
    {
        Assert.False(SupportCostVerdict.TryHostedScale(SupportGateCostTests.HostedSection(10, true, 39),
            SupportGateCostTests.HostedSection(20, false, 39), out HostedScale scale));
        Assert.Equal(default, scale);
        Assert.True(SupportCostVerdict.TryHostedScale(SupportGateCostTests.HostedSection(10, true, 40),
            SupportGateCostTests.HostedSection(20, false, 40), out scale));
        Assert.Equal(new HostedScale(2, 40, 2, 2), scale);
    }

    [Fact]
    public void HostedScaleIgnoresRowsMissingOrFailedInEitherSection()
    {
        JsonObject reference = SupportGateCostTests.HostedSection(10, true), hosted = SupportGateCostTests.HostedSection(20);
        // Only hosted records this row.
        hosted["Samples"]!.AsArray().Add(Extra(hosted, "flat box", 900));
        // The M2 sample failed its geometry.
        JsonObject failed = Extra(reference, "legacy row 1", 10);
        failed["GeometryPassed"] = false;
        reference["Samples"]!.AsArray().Add(failed);
        hosted["Samples"]!.AsArray().Add(Extra(hosted, "legacy row 1", 900));
        // The hosted sample was refused.
        reference["Samples"]!.AsArray().Add(Extra(reference, "legacy row 2", 10));
        JsonObject refused = Extra(hosted, "legacy row 2", 900);
        refused["Refusal"] = true;
        hosted["Samples"]!.AsArray().Add(refused);
        // The M2 sample is too short to time.
        reference["Samples"]!.AsArray().Add(Extra(reference, "legacy row 3", 0.5));
        hosted["Samples"]!.AsArray().Add(Extra(hosted, "legacy row 3", 900));
        Assert.True(SupportCostVerdict.TryHostedScale(reference, hosted, out HostedScale scale));
        Assert.Equal(new HostedScale(2, SupportGateCostTests.HostedRows, 2, 2), scale);
    }

    static JsonObject Extra(JsonObject section, string scene, double time)
    {
        JsonObject row = section["Samples"]![0]!.DeepClone().AsObject();
        row["Scene"] = scene;
        row["Operation"] = "LegacyDownPass";
        row["WarmMicroseconds"] = time;
        return row;
    }

    [Fact]
    public void OracleCorrectRefusalIsAnEnvelopeRow()
    {
        SupportCostSample find = NoSupport() with { OracleOutcome = "None", GeometryMatchesOracle = true };
        Assert.Equal(SupportCostGate.Envelope, SupportCostVerdict.Evaluate(find, find, false));
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(find with { Refusal = true }, find, false));
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(find with { Outcome = "Refused" }, find, false));
    }

    [Fact]
    public void OracleMismatchStaysAGeometryFailure()
    {
        SupportCostSample find = NoSupport() with { OracleOutcome = "Walkable", OracleHeight = 0, GeometryMatchesOracle = false };
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(find, find, false));
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(find with { OracleOutcome = "None" }, find, false));
    }

    [Theory]
    [InlineData("GroundSubstep/DryTick/0", new byte[] { 2 })]
    [InlineData("DryTick", new byte[] { 2, 2, 0, 2 })]
    [InlineData("ExplicitTick", new byte[] { 0 })]
    public void OracleBlockedStationarySubstepIsAnEnvelopeRow(string operation, byte[] statuses)
    {
        SupportCostSample blocked = Blocked(operation, statuses);
        Assert.Equal(SupportCostGate.Envelope, SupportCostVerdict.Evaluate(blocked, blocked, false, 1, 624));
    }

    [Theory]
    [InlineData("walkable read")]
    [InlineData("refused read")]
    [InlineData("no reads")]
    [InlineData("mismatch")]
    [InlineData("refusal")]
    [InlineData("moved")]
    [InlineData("vertical motion")]
    [InlineData("other operation")]
    public void StationaryRowWithoutAnOracleBlockStaysARejection(string fault)
    {
        SupportCostSample row = Blocked("GroundSubstep/DryTick/0", [2, 2]);
        row = fault switch
        {
            "walkable read" => Blocked(row.Operation, [2, 1]),
            "refused read" => Blocked(row.Operation, [2, 3]),
            "no reads" => Blocked(row.Operation, []),
            "mismatch" => row with { GeometryMatchesOracle = false },
            "refusal" => row with { Refusal = true },
            "moved" => row with { AchievedMotion = Vector3.UnitX, WarmMicroseconds = 201 },
            "vertical motion" => row with { AchievedMotion = Vector3.UnitY },
            _ => row with { Operation = "GroundTickBorrowed" },
        };
        Assert.Equal(SupportCostGate.Reject, SupportCostVerdict.Evaluate(row, row, false));
    }

    [Fact]
    public void ArtifactRecordsEnvelopeRowsOutsideTheGate()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SupportCostSample find = NoSupport() with { OracleOutcome = "None", GeometryMatchesOracle = true };
            SupportCostSample mismatch = find with { Scene = "mismatch", GeometryMatchesOracle = false };
            SupportCostArtifact.Write(path, [find, find with { Operation = "LegacyDownPass" }, mismatch,
                mismatch with { Operation = "LegacyDownPass" }], "base", HostedSha, "command");
            JsonNode samples = JsonNode.Parse(File.ReadAllText(path))!["baseline"]!["Samples"]!;
            Assert.True(samples[0]!["Envelope"]!.GetValue<bool>());
            Assert.Null(samples[0]!["GatePassed"]);
            Assert.False(samples[2]!["Envelope"]!.GetValue<bool>());
            Assert.False(samples[2]!["GatePassed"]!.GetValue<bool>());
        }
        finally { File.Delete(path); }
    }

    static SupportCostSample NoSupport() => new("Find", "fixture", false, "None", double.NaN, double.NaN,
        Vector3.Zero, 0, default, 0, 0, 1, 2, 0, 4352)
    { OracleHeight = double.NaN };

    static SupportCostSample Blocked(string operation, byte[] statuses) => SupportGateCostTests.Synthetic(operation, 2) with
    {
        GeometryPassed = false,
        Outcome = operation.StartsWith("GroundSubstep", StringComparison.Ordinal) ? "SupportedSubstep" : "Grounded",
        AchievedMotion = Vector3.Zero,
        GeometryMatchesOracle = true,
        SupportReads = Array.ConvertAll(statuses, status => new SupportFindObservation(Vector2.UnitX, 0.1f, 0.2f, 0.4f, 0.4f,
            0.67f, status, status == 0 ? float.NaN : 0.15f, status == 0 ? float.NaN : 0)),
    };

    static int Finalize(string artifact, int status, bool expectedSuccess = false)
    {
        string? root = Directory.GetCurrentDirectory();
        while (root is not null && !File.Exists(Path.Combine(root, "scripts", "record-support-cost-exit.py")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var info = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(root!, "scripts", "record-support-cost-exit.py"));
        info.ArgumentList.Add(artifact);
        info.ArgumentList.Add(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using Process process = Process.Start(info)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (expectedSuccess) Assert.True(process.ExitCode == 0, error);
        if (process.ExitCode == 0) Assert.Equal("", error);
        return process.ExitCode;
    }
}
