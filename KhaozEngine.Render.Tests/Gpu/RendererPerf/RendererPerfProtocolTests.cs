using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfProtocolTests
{
    const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Digest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string OtherSha = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Constructed_recipe_identities_satisfy_the_accepted_manifest_contract()
    {
        JsonObject json = ManifestJson("full");
        foreach (var (id, ordinal) in new[] { ("empty", 0), ("village", 1), ("crowd", 3) })
        {
            using var recipe = RendererPerfRecipe.Create(id);
            RendererPerfRecipeFacts facts = recipe.Facts;
            using JsonDocument description = JsonDocument.Parse(recipe.Describe());
            Assert.Equal(facts.Profile, description.RootElement.GetProperty("Profile").GetString());
            JsonNode identity = json["recipes"]![ordinal]!;
            identity["version"] = facts.Id + "/" + facts.Version;
            identity["profile"] = facts.Profile;
            identity["recipeSha256"] = facts.RecipeSha256;
            identity["settingsSha256"] = facts.SettingsSha256;
            identity["geometrySha256"] = facts.GeometrySha256;
            identity["assetSha256"] = facts.AssetSha256;
        }
        RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    [Theory]
    [InlineData("duplicate", "Json")]
    [InlineData("nested-duplicate", "Json")]
    [InlineData("unknown", "Json")]
    [InlineData("missing", "Json")]
    [InlineData("mistyped", "Json")]
    [InlineData("null", "Json")]
    [InlineData("nan", "Json")]
    [InlineData("overflow", "Json")]
    [InlineData("coverage", "Schema")]
    [InlineData("bad-hash", "Hash")]
    [InlineData("manifest-hash", "Hash")]
    [InlineData("workload-hash", "Authorization")]
    [InlineData("attempt", "Authorization")]
    [InlineData("parents", "Authorization")]
    [InlineData("parent-sha", "Authorization")]
    [InlineData("extra-file", "Authorization")]
    [InlineData("wrong-path", "Authorization")]
    [InlineData("modified", "Authorization")]
    [InlineData("reused", "Authorization")]
    [InlineData("leg", "Authorization")]
    [InlineData("runner", "Authorization")]
    [InlineData("workflow", "Authorization")]
    [InlineData("budget", "Preset")]
    [InlineData("approval", "Approval")]
    [InlineData("campaign-cap", "Approval")]
    [InlineData("block-order", "Preset")]
    [InlineData("control-order", "Preset")]
    [InlineData("count", "Preset")]
    [InlineData("limit", "Preset")]
    [InlineData("pacing", "Preset")]
    [InlineData("software-policy", "Preset")]
    [InlineData("recipe-null", "Json")]
    public void Rejects_invalid_or_reused_authorization_before_factory_or_clock(string fault, string code)
    {
        JsonObject json = ManifestJson();
        switch (fault)
        {
            case "unknown": json["adaptiveRetries"] = 1; break;
            case "missing": json.Remove("approval"); break;
            case "mistyped": json["batchSize"] = "3"; break;
            case "null": json["unitId"] = null; break;
            case "coverage": json["schema"] = "khaozengine.coverage-perf-unit/1"; break;
            case "bad-hash": json["workloadSha"] = "not-a-sha"; break;
            case "budget": json["budget"]!["maximumCostUsd"] = 7.24; break;
            case "approval": json["approval"]!["reference"] = " "; break;
            case "campaign-cap": json["approval"]!["campaignMaximumUsd"] = 10.01; break;
            case "block-order": ((JsonArray)json["blocks"]!).ReverseInPlace(); break;
            case "control-order": ((JsonArray)json["controls"]!).ReverseInPlace(); break;
            case "count": json["blocks"]![0]!["measuredFrames"] = 129; break;
            case "limit": json["limits"]!["unitSeconds"] = 601; break;
            case "pacing": json["batchSize"] = 4; break;
            case "software-policy": json["execution"]!["adapterPolicy"] = "any"; break;
            case "recipe-null": json["recipes"]![0] = null; break;
        }
        string text = json.ToJsonString();
        text = fault switch
        {
            "duplicate" => text.Insert(1, "\"unitId\":\"other\","),
            "nested-duplicate" => text.Replace("\"unitSeconds\":600", "\"unitSeconds\":600,\"unitSeconds\":600", StringComparison.Ordinal),
            "nan" => text.Replace("0.052", "NaN", StringComparison.Ordinal),
            "overflow" => text.Replace("\"batchSize\":3", "\"batchSize\":2147483648", StringComparison.Ordinal),
            _ => text
        };
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        RendererPerfAuthorization auth = Authorization(bytes);
        auth = fault switch
        {
            "manifest-hash" => auth with { ManifestSha256 = Digest },
            "workload-hash" => auth with { WorkloadSha = OtherSha },
            "attempt" => auth with { RunAttempt = 2 },
            "parents" => auth with { ParentCount = 2 },
            "parent-sha" => auth with { ParentSha = OtherSha },
            "extra-file" => auth with { ChangedPaths = ["tools/Renderer.Perf.Tests/authorized/pilot-linux.json", "other.cs"] },
            "wrong-path" => auth with { ChangedPaths = ["tools/Coverage.Perf.Tests/authorized/pilot-linux.json"] },
            "modified" => auth with { ManifestChangeKind = "M" },
            "reused" => auth with { PreviousRunId = "122" },
            "leg" => auth with { Leg = "windows-d3d11" },
            "runner" => auth with { Runner = "self-hosted" },
            "workflow" => auth with { Workflow = "other.yml" },
            _ => auth
        };

        int factoryCalls = 0, clockCalls = 0;
        RendererPerfProtocolException error = Assert.Throws<RendererPerfProtocolException>(() =>
        {
            _ = RendererPerfManifest.Parse(bytes, auth);
            clockCalls++;
            factoryCalls++;
        });
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        if (code == "Json") Assert.NotNull(error.InnerException);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, clockCalls);
    }

    [Fact]
    public void Accepts_the_owner_campaign_envelope_of_ten_dollars()
    {
        JsonObject json = ManifestJson();
        json["approval"]!["campaignMaximumUsd"] = 10;
        RendererPerfManifest manifest = RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Equal(10m, manifest.Approval.CampaignMaximumUsd);
    }

    [Fact]
    public void Preserves_missing_metric_status_and_partial_failure()
    {
        RendererPerfReport report = PartialReport();
        byte[] bytes = RendererPerfReportJson.Serialize(report);
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement metric = json.RootElement.GetProperty("raw").GetProperty("metrics").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "gpu");
        Assert.Equal(0, json.RootElement.GetProperty("assessment").GetProperty("metrics").GetProperty("values").GetArrayLength());
        Assert.Equal("unsupported", metric.GetProperty("status").GetString());
        Assert.Equal("no timestamp counter", metric.GetProperty("reason").GetString());
        RendererPerfReport restored = RendererPerfReportJson.Parse(bytes);
        Assert.Equal("device lost", restored.Publication.OriginalFailure);
        Assert.Equal(new[] { "cleanup failed", "publication failed" }, restored.Publication.Errors.ToArray());
        Assert.False(restored.Publication.FinalUnitPublished);
        Assert.Equal(RendererPerfPublicationState.Failed, restored.Publication.State);
        Assert.Equal(ulong.MaxValue, restored.Pipelines[0].Executables[0].Statistics[0].Metric.Value!.UInt64);
        Assert.Equal(GpuDiagnosticProvenance.IntermediateCode, restored.Pipelines[0].Executables[0].Statistics[0].Metric.Provenance);
        Assert.Null(restored.Pipelines[0].Executables[0].PhysicalRegisters.Value);
        Assert.Null(restored.Memory.Native[0].ResidentBytes.Value);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report with
        {
            Publication = report.Publication with { State = RendererPerfPublicationState.Completed, FinalUnitPublished = true }
        }));
    }

    [Theory]
    [InlineData("pilot", "linux-vulkan", 1260, 0.624)]
    [InlineData("pilot", "windows-d3d11", 1260, 1.224)]
    [InlineData("full", "linux-vulkan", 16944, 1.820)]
    [InlineData("full", "windows-d3d11", 16944, 3.570)]
    [InlineData("full", "windows-vulkan", 16944, 3.570)]
    public void Round_trips_exact_scene_and_block_order(string preset, string leg, int frames, double maximumCost)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ManifestJson(preset, leg).ToJsonString());
        RendererPerfManifest manifest = RendererPerfManifest.Parse(bytes, Authorization(bytes) with
        {
            Leg = leg,
            Runner = leg == "linux-vulkan" ? "gpu-t4-linux" : "gpu-t4-windows"
        });
        Assert.Equal(frames, manifest.TotalFrameCount);
        Assert.Equal((decimal)maximumCost, manifest.Budget.MaximumCostUsd);
        string[] order = manifest.Blocks.Select(b => $"{b.SceneId}/{b.Iteration}/{b.Layout}").ToArray();
        Assert.Equal(preset == "pilot" ? ["S3/0/TotalOnly", "S3/0/PerPass"] : FullOrder, order);
        Assert.Equal(preset == "pilot" ? [1, 4, 5, 7, 9, 11] : Enumerable.Range(1, 12), manifest.Controls.Select(c => c.Id));
        RendererPerfReport report = PartialReport() with { Manifest = manifest };
        RendererPerfReport restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.Equal(order, restored.Manifest.Blocks.Select(b => $"{b.SceneId}/{b.Iteration}/{b.Layout}"));
        Assert.Equal(manifest.Recipes.Select(r => r.SceneId), restored.Manifest.Recipes.Select(r => r.SceneId));
    }

    [Theory]
    [InlineData("available-null")]
    [InlineData("missing-reason")]
    [InlineData("unavailable-zero")]
    [InlineData("nonfinite")]
    [InlineData("physical-register-lie")]
    [InlineData("typed-value")]
    [InlineData("wrong-unit")]
    [InlineData("physical-register-bool")]
    [InlineData("null-map-value")]
    public void Invalid_metrics_cannot_be_serialized_as_success(string fault)
    {
        RendererPerfReport report = PartialReport();
        RendererPerfMetric metric = report.Assessment.Metrics["gpu"];
        metric = fault switch
        {
            "available-null" => metric with { Status = RendererPerfMetricStatus.Available, Reason = null },
            "missing-reason" => metric with { Reason = null },
            "unavailable-zero" => metric with { Value = 0 },
            "nonfinite" => metric with { Status = RendererPerfMetricStatus.Available, Value = double.NaN, Reason = null },
            _ => metric
        };
        report = report with { Assessment = report.Assessment with { Metrics = report.Assessment.Metrics.SetItem("gpu", metric) } };
        if (fault == "null-map-value")
            report = report with { Assessment = report.Assessment with { Metrics = report.Assessment.Metrics.SetItem("gpu", null!) } };
        GpuPipelineExecutableReport executable = report.Pipelines[0].Executables[0];
        if (fault == "physical-register-lie")
            executable = executable with { PhysicalRegisters = executable.Statistics[0].Metric };
        if (fault == "typed-value")
            executable = executable with
            {
                PhysicalRegisters = executable.Statistics[0].Metric with
                {
                    Provenance = GpuDiagnosticProvenance.VendorCompiler,
                    Unit = "registers",
                    Value = new(GpuDiagnosticValueType.UInt64, UInt64: 7, Float64: 8)
                }
            };
        if (fault is "wrong-unit" or "physical-register-bool")
            executable = executable with
            {
                PhysicalRegisters = executable.Statistics[0].Metric with
                {
                    Provenance = GpuDiagnosticProvenance.VendorCompiler,
                    Unit = fault == "wrong-unit" ? "slots" : "registers",
                    Value = fault == "wrong-unit" ? new(GpuDiagnosticValueType.UInt64, UInt64: 7) : new(GpuDiagnosticValueType.Boolean, Boolean: true)
                }
            };
        report = report with { Pipelines = [report.Pipelines[0] with { Executables = [executable] }] };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report));
    }

    [Fact]
    public void Metric_dictionary_keys_cannot_be_mistaken_for_a_metric_object()
    {
        RendererPerfReport report = PartialReport();
        RendererPerfMetric metric = report.Assessment.Metrics["gpu"];
        report = report with
        {
            Assessment = report.Assessment with
            {
                Metrics = report.Assessment.Metrics.Add("value", metric).Add("status", metric).Add("unit", metric)
            }
        };
        RendererPerfReport restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.Equal(4, restored.Assessment.Metrics.Count);
    }

    [Fact]
    public void Final_report_requires_every_frame_and_first_attempt_hardware_identity()
    {
        RendererPerfReport report = CompleteReport();
        byte[] bytes = RendererPerfReportJson.Serialize(report);
        Assert.True(RendererPerfReportJson.Parse(bytes).Publication.FinalUnitPublished);
        var json = JsonNode.Parse(bytes)!.AsObject();
        ((JsonArray)json["blocks"]![1]!["measured"]!).RemoveAt(127);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        json = JsonNode.Parse(bytes)!.AsObject();
        ((JsonArray)json["controls"]![5]!["measured"]!).RemoveAt(63);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report with { Identity = report.Identity with { Attempt = 2 } }));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report with { Identity = report.Identity with { SoftwareAdapter = true } }));
    }

    [Theory]
    [InlineData("missing-frame-field")]
    [InlineData("invalid-gpu")]
    [InlineData("frame-error")]
    public void Incomplete_or_invalid_frames_cannot_be_promoted(string fault)
    {
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(CompleteReport()))!.AsObject();
        JsonObject frame = json["blocks"]![0]!["measured"]![0]!.AsObject();
        if (fault == "missing-frame-field") frame.Remove("errors");
        if (fault == "invalid-gpu")
        {
            RendererPerfRawTestJson.Unavailable(json, frame["gpuFrame"]!, "disjoint timestamp query");
            frame["gpuElapsedTicks"] = null;
        }
        if (fault == "frame-error") ((JsonArray)frame["errors"]!).Add("unexpected production wait");
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("inconsistent")]
    [InlineData("wrong-unit")]
    [InlineData("conversion-overflow")]
    [InlineData("tiny-nonzero-rounded-to-zero")]
    [InlineData("outside-rounding-tolerance")]
    public void Invalid_outer_gpu_durations_cannot_be_promoted(string fault)
    {
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(CompleteReport()))!.AsObject();
        JsonNode frame = json["blocks"]![0]!["measured"]![0]!;
        switch (fault)
        {
            case "negative": frame["gpuFrame"]!["value"] = -1; break;
            case "inconsistent": frame["gpuFrame"]!["value"] = 11; break;
            case "wrong-unit": RendererPerfRawTestJson.Set(json, frame["gpuFrame"]!, "unit", "ms"); break;
            case "conversion-overflow": frame["gpuPeriodNs"] = 1e308; break;
            case "tiny-nonzero-rounded-to-zero":
                frame["gpuElapsedTicks"] = 1;
                frame["gpuPeriodNs"] = 1e-300;
                frame["gpuFrame"]!["value"] = 0;
                break;
            case "outside-rounding-tolerance": frame["gpuFrame"]!["value"] = 10.00000000002; break;
        }
        RendererPerfProtocolException error = Assert.Throws<RendererPerfProtocolException>(
            () => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal("Sample", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData(10.000000000005, 10ul, 1.0)]
    [InlineData(3.0, 12ul, 0.25)]
    [InlineData(1e-300, 1ul, 1e-300)]
    public void Outer_gpu_duration_retains_valid_raw_conversion(double duration, ulong ticks, double period)
    {
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(CompleteReport()))!.AsObject();
        JsonNode frame = json["blocks"]![0]!["measured"]![0]!;
        frame["gpuFrame"]!["value"] = duration;
        frame["gpuElapsedTicks"] = ticks;
        frame["gpuPeriodNs"] = period;
        RendererPerfReport restored = RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.True(restored.Assessment.ValidForPromotion);
        Assert.Equal(duration, restored.Blocks[0].Measured[0].GpuFrame.Value);
        Assert.Equal(ticks, restored.Blocks[0].Measured[0].GpuElapsedTicks);
        Assert.Equal(period, restored.Blocks[0].Measured[0].GpuPeriodNs);
    }

    [Fact]
    public void Generic_metrics_preserve_signed_values()
    {
        RendererPerfReport report = PartialReport();
        report = report with
        {
            Assessment = report.Assessment with
            {
                Metrics = report.Assessment.Metrics.Add("delta", new(-5, "ns", "comparison", RendererPerfMetricStatus.Available, null))
            }
        };
        RendererPerfReport restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.Equal(-5, restored.Assessment.Metrics["delta"].Value);
    }

    [Theory]
    [InlineData("missing-ticks")]
    [InlineData("missing-period")]
    [InlineData("inconsistent")]
    [InlineData("negative")]
    [InlineData("wrong-unit")]
    [InlineData("conversion-overflow")]
    public void Available_pass_duration_requires_raw_timing(string fault)
    {
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(CompleteReport()))!.AsObject();
        JsonNode frame = json["blocks"]![1]!["measured"]![0]!;
        JsonNode pass = JsonSerializer.SerializeToNode(new RendererPerfPassSample("opaque", 0, null, null, 1, 2, 10,
            new(10, "ns", "pass timestamp pair", RendererPerfMetricStatus.Available, null), false), RendererPerfManifest.JsonOptions)!;
        pass = RendererPerfRawTestJson.Pass(json, pass);
        ((JsonArray)frame["passes"]!).Add(pass);
        switch (fault)
        {
            case "missing-ticks": pass[1] = new JsonArray(); break;
            case "missing-period": frame["gpuPeriodNs"] = null; break;
            case "inconsistent": pass[3]![0] = 11; break;
            case "negative": pass[3]![0] = -1; break;
            case "wrong-unit":
                var scalar = new JsonObject { ["schema"] = pass[2]!.DeepClone(), ["value"] = pass[3]![0]!.DeepClone() };
                RendererPerfRawTestJson.Set(json, scalar, "unit", "ms");
                pass[2] = scalar["schema"]!.DeepClone(); break;
            case "conversion-overflow": frame["gpuPeriodNs"] = 1e308; break;
        }
        if (fault is "missing-period" or "conversion-overflow")
        {
            RendererPerfRawTestJson.Unavailable(json, frame["gpuFrame"]!, "outer query invalid");
            json["assessment"]!["validForPromotion"] = false;
        }
        RendererPerfProtocolException error = Assert.Throws<RendererPerfProtocolException>(
            () => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal("Sample", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Available_pass_duration_uses_retained_frame_period()
    {
        RendererPerfReport report = CompleteReport();
        RendererPerfBlockReport block = report.Blocks[1];
        RendererPerfFrameSample frame = block.Measured[0] with
        {
            GpuPeriodNs = 0.25,
            GpuFrame = block.Measured[0].GpuFrame with { Value = 2.5 },
            Passes = [new("opaque", 0, null, null, 1, 2, 8, new(2, "ns", "pass timestamp pair", RendererPerfMetricStatus.Available, null), false)]
        };
        report = report with { Blocks = report.Blocks.SetItem(1, block with { Measured = block.Measured.SetItem(0, frame) }) };
        RendererPerfReport restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.True(restored.Assessment.ValidForPromotion);
        Assert.Equal(8ul, restored.Blocks[1].Measured[0].Passes[0].ElapsedTicks);
        Assert.Equal(0.25, restored.Blocks[1].Measured[0].GpuPeriodNs);
        Assert.Equal(2, restored.Blocks[1].Measured[0].Passes[0].Duration.Value);
    }

    [Theory]
    [InlineData((int)RendererPerfMetricStatus.Unsupported)]
    [InlineData((int)RendererPerfMetricStatus.Invalid)]
    [InlineData((int)RendererPerfMetricStatus.BelowResolution)]
    [InlineData((int)RendererPerfMetricStatus.NotExecuted)]
    public void Unavailable_pass_timing_preserves_status_and_reason(int statusValue)
    {
        var status = (RendererPerfMetricStatus)statusValue;
        RendererPerfReport report = CompleteReport();
        RendererPerfBlockReport block = report.Blocks[1];
        RendererPerfFrameSample frame = block.Measured[0] with
        {
            Passes = [new("opaque", 0, null, null, 1, 2, null, new(null, "ns", "pass timestamp pair", status, "no usable pass duration"), false)]
        };
        report = report with
        {
            Blocks = report.Blocks.SetItem(1, block with { Measured = block.Measured.SetItem(0, frame) }),
            Assessment = report.Assessment with { ValidForPromotion = false, Reasons = ["pass timing unavailable"] }
        };
        RendererPerfReport restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        RendererPerfPassSample pass = restored.Blocks[1].Measured[0].Passes[0];
        Assert.Null(pass.ElapsedTicks);
        Assert.Null(pass.Duration.Value);
        Assert.Equal(status, pass.Duration.Status);
        Assert.Equal("no usable pass duration", pass.Duration.Reason);
    }

    [Theory]
    [InlineData("null-component")]
    [InlineData("unexplained-absent-bounds")]
    [InlineData("missing-minimum")]
    [InlineData("missing-maximum")]
    [InlineData("reversed-bounds")]
    [InlineData("negative-minimum")]
    [InlineData("negative-maximum")]
    [InlineData("nonfinite-minimum")]
    [InlineData("nonfinite-maximum")]
    [InlineData("missing-unit")]
    [InlineData("missing-source")]
    [InlineData("missing-provenance")]
    [InlineData("invalid-provenance")]
    [InlineData("missing-component-key")]
    [InlineData("missing-pass-id")]
    [InlineData("negative-occurrence")]
    [InlineData("duplicate-model")]
    [InlineData("missing-dependency-id")]
    [InlineData("missing-evidence-reason")]
    public void Invalid_work_model_components_are_rejected(string fault)
    {
        RendererPerfWorkRange component = new(10, 20, "bytes", "public recipe", "estimated", null);
        component = fault switch
        {
            "null-component" => null!,
            "unexplained-absent-bounds" => component with { Minimum = null, Maximum = null },
            "missing-minimum" => component with { Minimum = null },
            "missing-maximum" => component with { Maximum = null },
            "reversed-bounds" => component with { Minimum = 21 },
            "negative-minimum" => component with { Minimum = -1 },
            "negative-maximum" => component with { Minimum = -2, Maximum = -1 },
            "nonfinite-minimum" => component with { Minimum = double.NaN },
            "nonfinite-maximum" => component with { Maximum = double.PositiveInfinity },
            "missing-unit" => component with { Unit = " " },
            "missing-source" => component with { Source = " " },
            "missing-provenance" => component with { Provenance = " " },
            "invalid-provenance" => component with { Provenance = "measured" },
            _ => component
        };
        var model = new RendererPassWorkModel(fault == "missing-pass-id" ? " " : "opaque",
            fault == "negative-occurrence" ? -1 : 0,
            ImmutableDictionary<string, RendererPerfWorkRange>.Empty.Add(fault == "missing-component-key" ? " " : "bytes", component),
            fault == "missing-dependency-id" ? [" "] : [], fault == "missing-evidence-reason" ? [" "] : []);
        RendererPerfReport report = PartialReport();
        report = report with { Assessment = report.Assessment with { WorkModels = fault == "duplicate-model" ? [model, model] : [model] } };
        RendererPerfProtocolException error = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report));
        Assert.Equal(fault is "nonfinite-minimum" or "nonfinite-maximum" ? "Json" : "Metric", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData(0.0, 0.0, "exact", null)]
    [InlineData(10.0, 20.0, "estimated", null)]
    [InlineData(null, null, "unknown", "traffic unavailable")]
    [InlineData(10.0, null, "estimated", "maximum traffic unavailable")]
    [InlineData(null, 20.0, "unknown", "minimum traffic unavailable")]
    public void Work_model_components_preserve_ranges_and_missing_evidence(double? minimum, double? maximum, string provenance, string? reason)
    {
        RendererPerfReport report = PartialReport();
        var component = new RendererPerfWorkRange(minimum, maximum, "bytes", "public recipe", provenance, reason);
        var model = new RendererPassWorkModel("opaque", 0,
            ImmutableDictionary<string, RendererPerfWorkRange>.Empty.Add("bytes", component), ["upload"],
            reason == null ? [] : [reason]);
        report = report with { Assessment = report.Assessment with { WorkModels = [model] } };
        RendererPerfReport restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        Assert.Equal(component, restored.Assessment.WorkModels[0].Components["bytes"]);
        Assert.Equal(new[] { "upload" }, restored.Assessment.WorkModels[0].SerialDependencies.ToArray());
        Assert.Equal(reason == null ? [] : new[] { reason }, restored.Assessment.WorkModels[0].MissingEvidence.ToArray());
    }

    internal static RendererPerfReport CompleteReport()
    {
        RendererPerfReport report = PartialReport();
        RendererPerfMetric available = new(10, "ns", "outer timestamp pair", RendererPerfMetricStatus.Available, null);
        ImmutableArray<RendererPerfFrameSample> Frames(int count, int sequenceStart, int idStart) => Enumerable.Range(0, count)
            .Select(i => new RendererPerfFrameSample(idStart + i, (sequenceStart + i) % 256, i * 10, i * 10 + 5, 1000,
                10, 1, available, [], [], ImmutableDictionary<string, RendererPerfMetric>.Empty, [])).ToImmutableArray();
        report = report with
        {
            Compiler = RendererPerfCompilerFixture.Complete,
            Recipes = report.Manifest.Recipes.Select(r => new RendererPerfRecipeReport(r, "public primitives", Digest,
                ImmutableDictionary<string, long>.Empty, ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, RendererPerfMetric>.Empty)).ToImmutableArray(),
            Blocks = report.Manifest.Blocks.Select(b => new RendererPerfBlockReport(b,
                Frames(64, 192, 0), Frames(128, 0, 64), [])).ToImmutableArray(),
            Controls = report.Manifest.Controls.Select(c => new RendererPerfControlReport(c, Digest,
                Frames(32, 224, 0), Frames(64, 0, 32), ImmutableDictionary<string, RendererPerfMetric>.Empty)).ToImmutableArray(),
            Assessment = report.Assessment with { ValidForPromotion = true, Reasons = [] },
            Publication = report.Publication with
            {
                State = RendererPerfPublicationState.Completed,
                FinalUnitPublished = true,
                OriginalFailure = null,
                FailureText = new(true, 0, 0),
                Errors = []
            }
        };
        return RendererPerfExecutionEvidenceTests.WithTrace(report);
    }

    static readonly string[] FullOrder =
    [
        "S0/0/TotalOnly", "S0/0/PerPass", "S1/0/PerPass", "S1/0/TotalOnly", "S2/0/TotalOnly", "S2/0/PerPass",
        "S3/0/PerPass", "S3/0/TotalOnly", "S4/0/TotalOnly", "S4/0/PerPass",
        "S1/1/TotalOnly", "S1/1/PerPass", "S2/1/PerPass", "S2/1/TotalOnly", "S3/1/TotalOnly", "S3/1/PerPass",
        "S4/1/PerPass", "S4/1/TotalOnly", "S0/1/PerPass", "S0/1/TotalOnly",
        "S2/2/TotalOnly", "S2/2/PerPass", "S3/2/PerPass", "S3/2/TotalOnly", "S4/2/TotalOnly", "S4/2/PerPass",
        "S0/2/TotalOnly", "S0/2/PerPass", "S1/2/PerPass", "S1/2/TotalOnly"
    ];

    internal static JsonObject ManifestJson(string preset = "pilot", string leg = "linux-vulkan")
    {
        var root = JsonNode.Parse($$"""
        {
          "schema":"khaozengine.renderer-perf-unit/1", "unitId":"pilot-linux", "preset":"{{preset}}",
          "workloadSha":"{{Sha}}", "baselineMainSha":"{{Sha}}", "recipes":[], "blocks":[], "controls":[],
          "conditioningFrames":300, "batchSize":3, "maximumScopeOccurrences":255,
          "runtime":{"configuration":"Release","runtimeVersion":"10.0.0","environment":{},"gcMode":"workstation","tieredCompilation":true},
          "execution":{"backend":"vulkan","adapterPolicy":"nvidia-t4-hardware","runner":"gpu-t4-linux","workflow":"gpu-hosted-bench.yml","leg":"{{leg}}","completionPolicy":"retained-fence-batch-3/1"},
          "limits":{"unitSeconds":600,"frameMilliseconds":10000,"jobMinutes":12},
          "budget":{"maximumBilledMinutes":12,"ratePerMinute":0.052,"currency":"USD","maximumCostUsd":0.624},
          "approval":{"reference":"owner message 2026-10-21, first Linux pilot","purpose":"validate the renderer protocol","campaignReference":"owner message 2026-10-21, two pilots and two full units","campaignMaximumUsd":7.24}
        }
        """)!.AsObject();
        if (preset == "full")
        {
            root["conditioningFrames"] = 1200;
            root["limits"]!["unitSeconds"] = 1500;
            root["limits"]!["jobMinutes"] = 35;
            root["budget"]!["maximumBilledMinutes"] = 35;
            root["budget"]!["maximumCostUsd"] = leg == "linux-vulkan" ? 1.820m : 3.570m;
        }
        if (leg != "linux-vulkan")
        {
            root["execution"]!["runner"] = "gpu-t4-windows";
            root["execution"]!["backend"] = leg == "windows-d3d11" ? "d3d11" : "vulkan";
            root["budget"]!["ratePerMinute"] = 0.102m;
            if (preset == "pilot") root["budget"]!["maximumCostUsd"] = 1.224m;
        }
        foreach (int scene in preset == "pilot" ? [3] : new[] { 0, 1, 2, 3, 4 })
            ((JsonArray)root["recipes"]!).Add(new JsonObject
            {
                ["sceneId"] = $"S{scene}",
                ["version"] = new[] { "empty/1", "village/1", "outdoors/1", "crowd/1", "effects-ui/1" }[scene],
                ["profile"] = scene == 0 ? "empty-native-1080/1" : "renderer-full-native-1080/1",
                ["recipeSha256"] = Digest,
                ["settingsSha256"] = Digest,
                ["shaderSha256"] = Digest,
                ["geometrySha256"] = Digest,
                ["assetSha256"] = Digest
            });
        foreach (string entry in preset == "pilot" ? ["S3/0/TotalOnly", "S3/0/PerPass"] : FullOrder)
        {
            string[] fields = entry.Split('/');
            ((JsonArray)root["blocks"]!).Add(new JsonObject
            {
                ["sceneId"] = fields[0],
                ["iteration"] = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
                ["layout"] = fields[2] == "TotalOnly" ? "total-only" : "per-pass",
                ["warmupFrames"] = preset == "pilot" ? 64 : 192,
                ["measuredFrames"] = preset == "pilot" ? 128 : 256
            });
        }
        foreach (int id in preset == "pilot" ? [1, 4, 5, 7, 9, 11] : Enumerable.Range(1, 12))
            ((JsonArray)root["controls"]!).Add(new JsonObject
            {
                ["id"] = id,
                ["version"] = "1",
                ["workSha256"] = Digest,
                ["warmupFrames"] = preset == "pilot" ? 32 : 64,
                ["measuredFrames"] = preset == "pilot" ? 64 : 128
            });
        return root;
    }

    static RendererPerfAuthorization Authorization(byte[] bytes) => new(
        Convert.ToHexStringLower(SHA256.HashData(bytes)), Sha, OtherSha, Sha, 1,
        ["tools/Renderer.Perf.Tests/authorized/pilot-linux.json"], 1, "linux-vulkan", "gpu-t4-linux", "gpu-hosted-bench.yml");

    static RendererPerfReport PartialReport()
    {
        RendererPerfManifest manifest = RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(ManifestJson().ToJsonString()));
        var missing = new RendererPerfMetric(null, "ns", "native timestamp query", RendererPerfMetricStatus.Unsupported, "no timestamp counter");
        var unavailable = new GpuDiagnosticMetric(null, "bytes", "backend query", GpuDiagnosticStatus.Unsupported, "not exposed", GpuDiagnosticProvenance.Unknown);
        var raw = new GpuPipelineStatistic("TempRegisterCount", "DXBC temporary slots", new(
            new(GpuDiagnosticValueType.UInt64, UInt64: ulong.MaxValue), "slots", "D3D11 reflection",
            GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.IntermediateCode));
        var executable = new GpuPipelineExecutableReport("main", "vertex", "vs_main", 1, 1, 1,
            [raw], unavailable, unavailable, unavailable, unavailable, unavailable);
        return new("khaozengine.renderer-perf-report/2", manifest,
            new("pilot-linux", Digest, Sha, Sha, OtherSha, OtherSha, "123", 1, 42, Digest,
                "Linux", "hosted-image", "hosted CPU", "10.0.0", "workstation", true, ImmutableDictionary<string, string>.Empty,
                "NVIDIA T4", 0x10de, 0x1eb8, "driver-1", "vulkan", "native", false, 64, 1, true,
                "none", "none", "retained-fence-batch-3/1"),
            [], [], new([new(unavailable, unavailable, unavailable, unavailable, unavailable, unavailable,
                unavailable, unavailable, unavailable, unavailable, false)], ImmutableDictionary<string, RendererPerfMetric>.Empty),
            [new(RendererPerfCompilerFixture.Identity(Digest), GpuDiagnosticStatus.Available, null, [executable])],
            [], new(false, ["device lost"], ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("gpu", missing), []),
            new(RendererPerfPublicationState.Failed, true, false, "device lost", ["cleanup failed", "publication failed"], missing, missing, missing), []);
    }
}

internal static class RendererPerfJsonTestExtensions
{
    internal static void ReverseInPlace(this JsonArray array)
    {
        JsonNode?[] nodes = array.Reverse().Select(n => n?.DeepClone()).ToArray();
        array.Clear();
        foreach (JsonNode? node in nodes) array.Add(node);
    }
}
