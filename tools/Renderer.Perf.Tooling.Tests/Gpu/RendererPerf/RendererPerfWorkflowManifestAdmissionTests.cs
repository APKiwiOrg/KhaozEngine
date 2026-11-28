using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfWorkflowManifestAdmissionTests
{
    public static IEnumerable<object[]> PaidFaults()
    {
        foreach (string leg in new[] { "linux-vulkan", "windows-d3d11" })
            foreach (string fault in new[] { "zero-budget-and-cap", "rate", "rate-precision", "currency", "cost",
                "billed-string", "billed-fraction", "rate-string", "reference", "purpose", "campaignReference" })
                yield return [leg, fault];
    }

    [Theory]
    [MemberData(nameof(PaidFaults))]
    public void Select_refuses_paid_budgets_and_references_the_managed_policy_refuses(string leg, string fault)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        Prepare(fixture, "water-optics-v2", leg);
        JsonNode budget = fixture.Manifest["budget"]!;
        switch (fault)
        {
            case "zero-budget-and-cap":
                budget["maximumCostUsd"] = 0;
                fixture.Manifest["approval"]!["campaignMaximumUsd"] = 0;
                break;
            case "rate": budget["ratePerMinute"] = 0.053m; break;
            case "rate-precision": budget["ratePerMinute"] = leg == "linux-vulkan" ? 0.052000000000000001m : 0.102000000000000001m; break;
            case "currency": budget["currency"] = "NZD"; break;
            case "cost": budget["maximumCostUsd"] = budget["maximumCostUsd"]!.GetValue<decimal>() - 0.001m; break;
            case "billed-string": budget["maximumBilledMinutes"] = budget["maximumBilledMinutes"]!.ToJsonString(); break;
            case "billed-fraction": budget["maximumBilledMinutes"] = JsonNode.Parse(budget["maximumBilledMinutes"]!.ToJsonString() + ".0"); break;
            case "rate-string": budget["ratePerMinute"] = budget["ratePerMinute"]!.ToJsonString(); break;
            default: fixture.Manifest["approval"]![fault] = " \t"; break;
        }
        RefuseBeforeAllocation(workspace, fixture.Manifest.ToJsonString());
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Select_refuses_a_currency_array_on_paid_legs(string leg)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        Prepare(workspace.Fixture, "water-optics-v2", leg);
        workspace.Fixture.Manifest["budget"]!["currency"] = JsonNode.Parse("[\"USD\"]");
        RefuseBeforeAllocation(workspace, workspace.Fixture.Manifest.ToJsonString());
    }

    [Theory]
    [InlineData("cap-high-first")]
    [InlineData("cap-low-first")]
    [InlineData("cost-high-first")]
    [InlineData("cost-low-first")]
    [InlineData("approval")]
    [InlineData("budget")]
    [InlineData("nested")]
    public void Select_refuses_duplicate_fields_at_every_depth(string fault)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        Prepare(fixture, "water-optics-v2", "linux-vulkan");
        string text = fixture.Manifest.ToJsonString();
        text = fault switch
        {
            "cap-high-first" => text.Replace("\"campaignMaximumUsd\":7.382", "\"campaignMaximumUsd\":7.383,\"campaignMaximumUsd\":7.382", StringComparison.Ordinal),
            "cap-low-first" => text.Replace("\"campaignMaximumUsd\":7.382", "\"campaignMaximumUsd\":7.382,\"campaignMaximumUsd\":7.383", StringComparison.Ordinal),
            "cost-high-first" => text.Replace("\"maximumCostUsd\":1.976", "\"maximumCostUsd\":1.977,\"maximumCostUsd\":1.976", StringComparison.Ordinal),
            "cost-low-first" => text.Replace("\"maximumCostUsd\":1.976", "\"maximumCostUsd\":1.976,\"maximumCostUsd\":1.977", StringComparison.Ordinal),
            "nested" => text.Replace("\"id\":1,", "\"id\":1,\"id\":1,", StringComparison.Ordinal),
            _ => text.Replace($"\"{fault}\":", $"\"{fault}\":{fixture.Manifest[fault]!.ToJsonString()},\"{fault}\":", StringComparison.Ordinal)
        };
        Assert.NotEqual(fixture.Manifest.ToJsonString(), text);
        RefuseBeforeAllocation(workspace, text);
    }

    [Theory]
    [InlineData("preset", "Preset")]
    [InlineData("campaignMaximumUsd", "CampaignMaximumUsd")]
    [InlineData("execution", "Execution")]
    public void Select_requires_exact_manifest_field_names(string name, string variant)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        Prepare(workspace.Fixture, "water-optics-v2", "linux-vulkan");
        string text = workspace.Fixture.Manifest.ToJsonString().Replace($"\"{name}\":", $"\"{variant}\":", StringComparison.Ordinal);
        RefuseBeforeAllocation(workspace, text);
    }

    [Theory]
    [InlineData("null-entry")]
    [InlineData("NaN")]
    [InlineData("1e400")]
    [InlineData("1e100")]
    public void Select_refuses_null_array_entries_and_nonfinite_or_excessive_numbers(string fault)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        Prepare(fixture, "water-optics-v2", "linux-vulkan");
        if (fault == "null-entry") fixture.Manifest["recipes"]![0] = null;
        string text = fixture.Manifest.ToJsonString();
        if (fault != "null-entry") text = text.Replace("\"conditioningFrames\":1200", "\"conditioningFrames\":" + fault, StringComparison.Ordinal);
        RefuseBeforeAllocation(workspace, text);
    }

    [Theory]
    [MemberData(nameof(RendererPerfColdLimitPolicyTests.Classes), MemberType = typeof(RendererPerfColdLimitPolicyTests))]
    public void Select_accepts_every_managed_valid_execution_class(string preset, string leg)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        Prepare(fixture, preset, leg);
        RendererPerfRehearsalManifestTests.Parse(fixture.Manifest);
        fixture.Accept("Select", "matrix=");
    }

    static void Prepare(RendererPerfWorkflowContractTests.WorkflowFixture fixture, string preset, string leg)
    {
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
    }

    static void RefuseBeforeAllocation(RendererPerfWorkflowTrustTests.AdmissionWorkspace workspace, string text)
    {
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(text)));
        File.WriteAllText(workspace.Fixture.ManifestPath, text);
        workspace.Fixture.Environment["RENDERER_PERF_MANIFEST_SHA256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(workspace.Fixture.ManifestPath)));
        var result = workspace.Select();
        Assert.True(result.Code != 0, result.Output);
        Assert.DoesNotContain("matrix=", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.Output) && File.ReadAllText(workspace.Output).Contains("matrix=", StringComparison.Ordinal));
    }
}
