using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Inline audit records exercise the protocol only. They authorize no native session or measurement.
public sealed class CoveragePerfManifestTests
{
    const string WorkloadSha = "1111111111111111111111111111111111111111";
    const string WorkflowSha = "2222222222222222222222222222222222222222";
    const string Fixture = "coverage-typical-gpu/1";
    const string Profile = "full-1280x720/1";
    const string Valid = """
        {
          "schema": "khaozengine.coverage-perf-unit/1",
          "preset": "C0",
          "unitId": "inline-c0",
          "workloadSha": "1111111111111111111111111111111111111111",
          "baselineMainSha": "3333333333333333333333333333333333333333",
          "fixture": "coverage-typical-gpu/1",
          "profile": "full-1280x720/1",
          "configuration": "Release",
          "routes": { "ordinarySkinning": "gpu", "coveredSurface": "gpu-built" },
          "device": { "backend": "vulkan-native", "adapterPolicy": "lavapipe-cpu-only" },
          "batchFrames": 3,
          "runner": "ubuntu-26.04",
          "workflow": "stress-test",
          "leg": "ubuntu-26.04-vulkan-lavapipe",
          "iterations": 1,
          "load": 0,
          "limits": { "maxUnitSeconds": 300, "maxFrameMilliseconds": 10000, "jobMinutes": 20 },
          "authorization": {
            "identity": "coverage-gpu-era-perf-inline-only",
            "approvedBy": "inline fixture, no owner approval",
            "reference": "Protocol test only, no measurement authorized"
          },
          "blocks": [
            { "index": 0, "actors": 0, "condition": "DirectOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
            { "index": 1, "actors": 0, "condition": "SharedOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
            { "index": 2, "actors": 1, "condition": "SharedOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
            { "index": 3, "actors": 1, "condition": "SharedOn", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" }
          ]
        }
        """;

    [Theory]
    [InlineData("schema", "khaozengine.coverage-cost-manifest/1", "schema")]
    [InlineData("schema", "khaozengine.coverage-cost-pair/1", "schema")]
    [InlineData("schema", "khaozengine.coverage-cost-report/1", "schema")]
    [InlineData("schema", "khaozengine.coverage-cost-pair-report/1", "schema")]
    [InlineData("schema", "khaozengine.coverage-perf-unit/0", "schema")]
    [InlineData("identity", "coverage-pilot-20261018-a1-after-unit4-once", "authorization.identity")]
    [InlineData("identity", "coverage-gpu-era-perf-", "authorization.identity")]
    [InlineData("oldField", "false", "gpuSkinning")]
    [InlineData("oldField", "true", "gpuSkinning")]
    [InlineData("oldBlockField", "false", "gpuSkinning")]
    public void RetiredSchemasAndIdentitiesNeverOpenAFactoryOrReadAClock(string field, string value, string reason)
    {
        string json = field switch
        {
            "schema" => Replace(Valid, "khaozengine.coverage-perf-unit/1", value),
            "identity" => Replace(Valid, "coverage-gpu-era-perf-inline-only", value),
            "oldField" => Replace(Valid, "\"batchFrames\": 3", $"\"gpuSkinning\": {value}, \"batchFrames\": 3"),
            _ => Replace(Valid, "\"index\": 0", $"\"gpuSkinning\": {value}, \"index\": 0"),
        };
        // Parse and ValidateAuthorization deliberately have no factory or clock dependency.
        var error = Assert.Throws<InvalidDataException>(() => Authorize(json));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.Contains("retired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("workloadSha", "workloadSha")]
    [InlineData("unitId", "unitId")]
    [InlineData("manifestHash", "manifestSha256")]
    [InlineData("exactBytes", "manifestSha256")]
    [InlineData("runner", "runner")]
    [InlineData("workflow", "workflow")]
    [InlineData("leg", "leg")]
    [InlineData("processId", "processId")]
    [InlineData("identity", "authorization.identity")]
    [InlineData("retiredIdentity", "authorization.identity")]
    [InlineData("counts", "measuredFrames")]
    public void ApprovalIsBoundToShaLegCountsAndExactBytes(string change, string reason)
    {
        var identity = Identity(Valid);
        string json = change switch
        {
            "exactBytes" => Valid + "\n",
            "counts" => Replace(Valid, "\"measuredFrames\": 32", "\"measuredFrames\": 128"),
            _ => Valid,
        };
        identity = change switch
        {
            "workloadSha" => identity with { WorkloadSha = WorkflowSha },
            "unitId" => identity with { UnitId = "other-unit" },
            "manifestHash" => identity with { ManifestSha256 = new string('0', 64) },
            "runner" => identity with { Runner = "self-hosted" },
            "workflow" => identity with { Workflow = "gpu-hosted-bench" },
            "leg" => identity with { Leg = "linux-vulkan" },
            "processId" => identity with { ProcessId = 0 },
            "identity" => identity with { AuthorizationIdentity = "coverage-gpu-era-perf-other-approval" },
            "retiredIdentity" => identity with { AuthorizationIdentity = "coverage-pilot-20261018-a1-after-unit4-once" },
            _ => identity,
        };
        var error = Assert.Throws<InvalidDataException>(() => CoveragePerfManifest.Parse(Bytes(json)).ValidateAuthorization(identity));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"preset\": \"C0\"", "\"preset\": \"C9\"", "preset")]
    [InlineData("\"batchFrames\": 3", "\"batchFrames\": 3, \"batchFrames\": 3", "batchFrames")]
    [InlineData("\"batchFrames\": 3", "\"batchFrames\": 3, \"unknown\": 0", "unknown")]
    [InlineData("\"batchFrames\": 3,", "", "batchFrames")]
    [InlineData("\"batchFrames\": 3", "\"batchFrames\": \"3\"", "batchFrames")]
    [InlineData("\"batchFrames\": 3", "\"batchFrames\": 3.0", "batchFrames")]
    [InlineData("\"batchFrames\": 3", "\"batchFrames\": 3e0", "batchFrames")]
    [InlineData("\"batchFrames\": 3", "\"batchFrames\": 4", "batchFrames")]
    [InlineData("\"load\": 0", "\"load\": -0", "load")]
    [InlineData("\"load\": 0", "\"load\": 1", "load")]
    [InlineData("\"iterations\": 1", "\"iterations\": 2", "iterations")]
    [InlineData("\"iterations\": 1", "\"iterations\": true", "iterations")]
    [InlineData("\"maxUnitSeconds\": 300", "\"maxUnitSeconds\": 299", "limits.maxUnitSeconds")]
    [InlineData("\"maxUnitSeconds\": 300", "\"maxUnitSeconds\": 3601", "limits.maxUnitSeconds")]
    [InlineData("\"maxUnitSeconds\": 300", "\"maxUnitSeconds\": 1e309", "limits.maxUnitSeconds")]
    [InlineData("\"maxFrameMilliseconds\": 10000", "\"maxFrameMilliseconds\": 9999", "limits.maxFrameMilliseconds")]
    [InlineData("\"maxFrameMilliseconds\": 10000", "\"maxFrameMilliseconds\": 10001", "limits.maxFrameMilliseconds")]
    [InlineData("\"maxFrameMilliseconds\": 10000", "\"maxFrameMilliseconds\": -1", "limits.maxFrameMilliseconds")]
    [InlineData("\"jobMinutes\": 20", "\"jobMinutes\": 30", "limits.jobMinutes")]
    [InlineData("\"configuration\": \"Release\"", "\"configuration\": \"Debug\"", "configuration")]
    [InlineData("\"fixture\": \"coverage-typical-gpu/1\"", "\"fixture\": \"coverage-typical-gpu/0\"", "fixture")]
    [InlineData("\"profile\": \"full-1280x720/1\"", "\"profile\": \"colour-only\"", "profile")]
    [InlineData("\"ordinarySkinning\": \"gpu\"", "\"ordinarySkinning\": \"cpu\"", "routes.ordinarySkinning")]
    [InlineData("\"coveredSurface\": \"gpu-built\"", "\"coveredSurface\": \"cpu\"", "routes.coveredSurface")]
    [InlineData("\"backend\": \"vulkan-native\"", "\"backend\": \"recording-cpu\"", "device.backend")]
    [InlineData("\"backend\": \"vulkan-native\"", "\"backend\": \"direct3d11-native\"", "device.backend")]
    [InlineData("\"adapterPolicy\": \"lavapipe-cpu-only\"", "\"adapterPolicy\": \"any\"", "device.adapterPolicy")]
    [InlineData("\"runner\": \"ubuntu-26.04\"", "\"runner\": \"self-hosted\"", "runner")]
    [InlineData("\"workflow\": \"stress-test\"", "\"workflow\": \"gpu-hosted-bench\"", "workflow")]
    [InlineData("\"leg\": \"ubuntu-26.04-vulkan-lavapipe\"", "\"leg\": \"linux-vulkan\"", "leg")]
    [InlineData("\"workloadSha\": \"1111111111111111111111111111111111111111\"", "\"workloadSha\": \"1111111\"", "workloadSha")]
    [InlineData("\"baselineMainSha\": \"3333333333333333333333333333333333333333\"", "\"baselineMainSha\": null", "baselineMainSha")]
    [InlineData("\"unitId\": \"inline-c0\"", "\"unitId\": \"../inline-c0\"", "unitId")]
    [InlineData("\"approvedBy\": \"inline fixture, no owner approval\"", "\"approvedBy\": \" \"", "authorization.approvedBy")]
    [InlineData("\"reference\": \"Protocol test only, no measurement authorized\"", "\"reference\": \"\"", "authorization.reference")]
    [InlineData("\"authorization\": {", "\"authorization\": null, \"retiredAuthorization\": {", "retiredAuthorization")]
    [InlineData("\"ordinarySkinning\": \"gpu\"", "\"ordinarySkinning\": \"gpu\", \"ordinarySkinning\": \"gpu\"", "routes.ordinarySkinning")]
    [InlineData("\"backend\": \"vulkan-native\"", "\"backend\": \"vulkan-native\", \"unknown\": true", "device.unknown")]
    [InlineData("\"index\": 0", "\"index\": 0, \"index\": 0", "blocks[0].index")]
    [InlineData("\"index\": 0", "\"index\": 0, \"optional\": true", "blocks[0].optional")]
    public void StrictFieldsRejectDuplicatesUnknownsAndCoercion(string from, string to, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() => Authorize(Replace(Valid, from, to)));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workflowSha", "\"2222222222222222222222222222222222222222\"")]
    [InlineData("workflowRunId", "\"123456789\"")]
    [InlineData("workflowRunAttempt", "1")]
    public void RunFactsInTheManifestAreRefusedAsUnknownFields(string field, string value)
    {
        string json = Replace(Valid, "\"approvedBy\":", $"\"{field}\": {value}, \"approvedBy\":");
        var error = Assert.Throws<InvalidDataException>(() => Authorize(json));
        Assert.Contains($"authorization.{field} is an unknown field", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("approvedBy")]
    [InlineData("reference")]
    public void EveryApprovalTimeAuthorizationFieldIsRequired(string field)
    {
        var root = JsonNode.Parse(Valid)!.AsObject();
        root["authorization"]!.AsObject().Remove(field);
        var error = Assert.Throws<InvalidDataException>(() => Authorize(root.ToJsonString()));
        Assert.Contains($"authorization.{field} is required", error.Message, StringComparison.Ordinal);
    }

    // The manifest is committed before dispatch, so the run that executes it is recorded and never matched.
    [Theory]
    [InlineData("1", "2222222222222222222222222222222222222222", 1)]
    [InlineData("987654321", "4444444444444444444444444444444444444444", 7)]
    [InlineData("9223372036854775807", "1111111111111111111111111111111111111111", 42)]
    public void AValidManifestValidatesAgainstAnyRunOfItsFirstAttempt(string runId, string workflowSha, int processId)
    {
        var identity = Identity(Valid) with { WorkflowRunId = runId, WorkflowSha = workflowSha, ProcessId = processId };
        Assert.Null(Record.Exception(() => CoveragePerfManifest.Parse(Bytes(Valid)).ValidateAuthorization(identity)));
    }

    [Theory]
    [InlineData("123456789", 2, "identity.workflowRunAttempt must be 1, a GitHub re-run cannot reuse an approval")]
    [InlineData("123456789", 3, "identity.workflowRunAttempt must be 1, a GitHub re-run cannot reuse an approval")]
    [InlineData("123456789", 0, "identity.workflowRunAttempt must be 1, a GitHub re-run cannot reuse an approval")]
    [InlineData("", 1, "identity.workflowRunId must be a positive decimal workflow run ID")]
    [InlineData("0", 1, "identity.workflowRunId must be a positive decimal workflow run ID")]
    [InlineData("0123", 1, "identity.workflowRunId must be a positive decimal workflow run ID")]
    [InlineData("-5", 1, "identity.workflowRunId must be a positive decimal workflow run ID")]
    [InlineData("run-1", 1, "identity.workflowRunId must be a positive decimal workflow run ID")]
    public void ReRunAttemptsAndMalformedRunIdsAreRefused(string runId, int attempt, string reason)
    {
        var identity = Identity(Valid) with { WorkflowRunId = runId, WorkflowRunAttempt = attempt };
        var error = Assert.Throws<InvalidDataException>(() => CoveragePerfManifest.Parse(Bytes(Valid)).ValidateAuthorization(identity));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C0", "none", "")]
    [InlineData("C1", "none", "")]
    [InlineData("G1", "none", "")]
    [InlineData("G2", "none", "")]
    [InlineData("G3", "none", "")]
    [InlineData("C0", "reverse", "condition")]
    [InlineData("C0", "missing", "blocks")]
    [InlineData("C1", "extra", "blocks")]
    [InlineData("C0", "index", "index")]
    [InlineData("C0", "actors", "actors")]
    [InlineData("C0", "negativeActors", "actors")]
    [InlineData("C0", "manyActors", "actors")]
    [InlineData("C1", "zeroActors", "actors")]
    [InlineData("C1", "oneActor", "actors")]
    [InlineData("C0", "zeroCovered", "actors")]
    [InlineData("C1", "warm", "warmupFrames")]
    [InlineData("C0", "odd", "measuredFrames")]
    [InlineData("C0", "even", "measuredFrames")]
    [InlineData("C0", "zeroFrames", "measuredFrames")]
    [InlineData("C1", "manyFrames", "measuredFrames")]
    [InlineData("C0", "layout", "timerLayout")]
    [InlineData("G1", "layout", "timerLayout")]
    [InlineData("G2", "reverse", "condition")]
    [InlineData("G3", "partial", "blocks")]
    public void OnlyApprovedBlockSequencesAreAccepted(string preset, string change, string reason)
    {
        string json = PresetJson(preset, change);
        if (change != "none")
        {
            var error = Assert.Throws<InvalidDataException>(() => Authorize(json));
            Assert.Contains(reason, error.Message, StringComparison.Ordinal);
            return;
        }
        var manifest = CoveragePerfManifest.Parse(Bytes(json));
        manifest.ValidateAuthorization(Identity(json, preset));
        Assert.Equal(preset, manifest.Preset);
        Assert.Equal(Fixture, manifest.Fixture);
        Assert.Equal(Profile, manifest.Profile);
        Assert.Equal(3, manifest.BatchFrames);
        Assert.Equal(1, manifest.Iterations);
        Assert.Equal(0, manifest.Load);
        Assert.Equal("Release", manifest.Configuration);
        Assert.Equal(10000, manifest.MaxFrameMilliseconds);
        Assert.Equal(preset switch { "C0" => 300, "C1" => 5400, _ => 600 }, manifest.MaxUnitSeconds);
        Assert.Equal(preset switch { "C0" => 20, "C1" => 120, _ => 30 }, manifest.JobMinutes);
        if (preset == "C0")
            Assert.Collection(manifest.Blocks,
                b => Block(b, 0, 0, CoveragePerfCondition.DirectOff, CoveragePerfTimerLayout.CpuOnly, 32),
                b => Block(b, 1, 0, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, 32),
                b => Block(b, 2, 1, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, 32),
                b => Block(b, 3, 1, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.CpuOnly, 32));
        else if (preset == "C1")
            Assert.Collection(manifest.Blocks,
                b => Block(b, 0, 32, CoveragePerfCondition.DirectOff, CoveragePerfTimerLayout.CpuOnly, 128),
                b => Block(b, 1, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, 128),
                b => Block(b, 2, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.CpuOnly, 128),
                b => Block(b, 3, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.CpuOnly, 128),
                b => Block(b, 4, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.CpuOnly, 128),
                b => Block(b, 5, 32, CoveragePerfCondition.DirectOff, CoveragePerfTimerLayout.CpuOnly, 128));
        else
            Assert.Collection(manifest.Blocks,
                b => Block(b, 0, 1, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Phases, 32),
                b => Block(b, 1, 1, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Phases, 32),
                b => Block(b, 2, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Phases, 32),
                b => Block(b, 3, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Phases, 32),
                b => Block(b, 4, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Total, 128),
                b => Block(b, 5, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Total, 128),
                b => Block(b, 6, 32, CoveragePerfCondition.SharedOn, CoveragePerfTimerLayout.Total, 128),
                b => Block(b, 7, 32, CoveragePerfCondition.SharedOff, CoveragePerfTimerLayout.Total, 128));
    }

    [Fact]
    public void ExactUtf8BytesAreRetainedAndWhitespaceNeedsItsOwnBinding()
    {
        byte[] bytes = Bytes(Valid);
        var manifest = CoveragePerfManifest.Parse(bytes);
        manifest.ValidateAuthorization(Identity(Valid));
        Assert.Equal(Hash(bytes), manifest.Sha256);
        Assert.Equal(bytes, manifest.Utf8);
        bytes[0] = 0;
        Assert.Equal((byte)'{', manifest.Utf8[0]);
        var spaced = CoveragePerfManifest.Parse(Bytes(Valid + "\n"));
        Assert.NotEqual(manifest.Sha256, spaced.Sha256);
        spaced.ValidateAuthorization(Identity(Valid + "\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schema\":NaN}")]
    [InlineData("{\"schema\":\"khaozengine.coverage-perf-unit/1\",}")]
    [InlineData("{/*comment*/\"schema\":\"khaozengine.coverage-perf-unit/1\"}")]
    public void MalformedDocumentsAreRefused(string json) =>
        Assert.Throws<InvalidDataException>(() => CoveragePerfManifest.Parse(Bytes(json)));

    [Fact]
    public void ExcessiveInputAndApprovalLabelsAreRefused()
    {
        Assert.Contains("bytes", Assert.Throws<InvalidDataException>(() => CoveragePerfManifest.Parse(new byte[65537])).Message);
        string json = Replace(Valid, "Protocol test only, no measurement authorized", new string('x', 1025));
        Assert.Contains("authorization.reference", Assert.Throws<InvalidDataException>(() => Authorize(json)).Message);
        Assert.Throws<InvalidDataException>(() => CoveragePerfManifest.Parse([0x7b, 0xff, 0x7d]));
    }

    [Fact]
    public void NullAndMissingApprovalBlocksAreRefused()
    {
        var root = JsonNode.Parse(Valid)!.AsObject();
        root["authorization"] = null;
        Assert.Contains("authorization", Assert.Throws<InvalidDataException>(() => Authorize(root.ToJsonString())).Message);
        root.Remove("authorization");
        Assert.Contains("authorization", Assert.Throws<InvalidDataException>(() => Authorize(root.ToJsonString())).Message);
    }

    static void Block(CoveragePerfBlock block, int index, int actors, CoveragePerfCondition condition,
        CoveragePerfTimerLayout layout, int measured)
    {
        Assert.Equal(index, block.Index);
        Assert.Equal(actors, block.Actors);
        Assert.Equal(condition, block.Condition);
        Assert.Equal(layout, block.TimerLayout);
        Assert.Equal(64, block.WarmupFrames);
        Assert.Equal(measured, block.MeasuredFrames);
        Assert.Equal(Fixture, block.Fixture);
    }

    static void Authorize(string json) => CoveragePerfManifest.Parse(Bytes(json)).ValidateAuthorization(Identity(json));

    static CoveragePerfRunIdentity Identity(string json, string preset = "C0") => new(
        WorkloadSha, WorkflowSha, "inline-c0", Hash(Bytes(json)),
        preset is "C0" or "C1" ? "ubuntu-26.04" : preset == "G1" ? "gpu-t4-linux" : "gpu-t4-windows",
        preset is "C0" or "C1" ? "stress-test" : "gpu-hosted-bench",
        preset switch { "G1" => "linux-vulkan", "G2" => "windows-d3d11", "G3" => "windows-vulkan", _ => "ubuntu-26.04-vulkan-lavapipe" },
        "123456789", 1, 42, "coverage-gpu-era-perf-inline-only");

    static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static string Replace(string json, string from, string to)
    {
        Assert.Contains(from, json, StringComparison.Ordinal);
        return json.Replace(from, to, StringComparison.Ordinal);
    }

    static string PresetJson(string preset, string change)
    {
        var root = JsonNode.Parse(Valid)!.AsObject();
        root["preset"] = preset;
        if (preset != "C0")
        {
            root["limits"]!["maxUnitSeconds"] = preset == "C1" ? 5400 : 600;
            root["limits"]!["jobMinutes"] = preset == "C1" ? 120 : 30;
        }
        if (preset == "C1") root["blocks"] = JsonNode.Parse(CpuProofBlocks);
        else if (preset is "G1" or "G2" or "G3")
        {
            root["blocks"] = JsonNode.Parse(GpuBlocks);
            root["runner"] = preset == "G1" ? "gpu-t4-linux" : "gpu-t4-windows";
            root["workflow"] = "gpu-hosted-bench";
            root["leg"] = preset switch { "G1" => "linux-vulkan", "G2" => "windows-d3d11", _ => "windows-vulkan" };
            root["device"]!["backend"] = preset == "G2" ? "direct3d11-native" : "vulkan-native";
            root["device"]!["adapterPolicy"] = "nvidia-t4";
        }
        var blocks = root["blocks"]!.AsArray();
        switch (change)
        {
            case "reverse": blocks[0]!["condition"] = "SharedOn"; break;
            case "missing": blocks.RemoveAt(0); break;
            case "extra": blocks.Add(blocks[0]!.DeepClone()); break;
            case "partial": blocks.RemoveAt(7); break;
            case "index": blocks[1]!["index"] = 0; break;
            case "actors": blocks[0]!["actors"] = 2; break;
            case "negativeActors": blocks[0]!["actors"] = -1; break;
            case "manyActors": blocks[0]!["actors"] = 128; break;
            case "zeroActors": blocks[0]!["actors"] = 0; break;
            case "oneActor": blocks[0]!["actors"] = 1; break;
            case "zeroCovered": blocks[3]!["actors"] = 0; break;
            case "warm": blocks[0]!["warmupFrames"] = 66; break;
            case "odd": blocks[0]!["measuredFrames"] = 31; break;
            case "even": blocks[0]!["measuredFrames"] = 34; break;
            case "zeroFrames": blocks[0]!["measuredFrames"] = 0; break;
            case "manyFrames": blocks[0]!["measuredFrames"] = 5002; break;
            case "layout": blocks[0]!["timerLayout"] = "Total"; break;
        }
        return root.ToJsonString();
    }

    const string CpuProofBlocks = """
        [
          { "index": 0, "actors": 32, "condition": "DirectOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 1, "actors": 32, "condition": "SharedOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 2, "actors": 32, "condition": "SharedOn", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 3, "actors": 32, "condition": "SharedOn", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 4, "actors": 32, "condition": "SharedOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 5, "actors": 32, "condition": "DirectOff", "timerLayout": "CpuOnly", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" }
        ]
        """;

    const string GpuBlocks = """
        [
          { "index": 0, "actors": 1, "condition": "SharedOff", "timerLayout": "Phases", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
          { "index": 1, "actors": 1, "condition": "SharedOn", "timerLayout": "Phases", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
          { "index": 2, "actors": 32, "condition": "SharedOff", "timerLayout": "Phases", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
          { "index": 3, "actors": 32, "condition": "SharedOn", "timerLayout": "Phases", "warmupFrames": 64, "measuredFrames": 32, "fixture": "coverage-typical-gpu/1" },
          { "index": 4, "actors": 32, "condition": "SharedOff", "timerLayout": "Total", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 5, "actors": 32, "condition": "SharedOn", "timerLayout": "Total", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 6, "actors": 32, "condition": "SharedOn", "timerLayout": "Total", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" },
          { "index": 7, "actors": 32, "condition": "SharedOff", "timerLayout": "Total", "warmupFrames": 64, "measuredFrames": 128, "fixture": "coverage-typical-gpu/1" }
        ]
        """;
}
