using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Xunit;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportCostArtifactTests
{
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
            Assert.Null(root["pendingFinalization"]);
            Assert.NotEqual(0, Finalize(path, 0));
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
