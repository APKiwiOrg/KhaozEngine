using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal static class ClothPreparationAdmission
{
    private const string FilterPrefix = "FullyQualifiedName=KhaozEngine.Tests.Render3D." +
        nameof(ClothPreparationCostDiagnostics) + ".";
    internal const string Filter = FilterPrefix +
        nameof(ClothPreparationCostDiagnostics.Report_close_cloth_preparation_cost);
    internal const string ComparisonFilter = FilterPrefix +
        nameof(ClothPreparationCostDiagnostics.Report_reused_factor_cloth_preparation_cost);

    internal static bool IsSmoke(string filter, Func<string, string?> read) => filter switch
    {
        Filter => read("KE_CLOTH_PREPARATION_SMOKE") == "1",
        ComparisonFilter => read("KE_CLOTH_FACTOR_COMPARISON_SMOKE") == "1" && read("KE_GPU_TESTS") == "0" &&
            string.IsNullOrEmpty(read("FILTER")),
        _ => false,
    };

    internal static bool IsSelected(string filter, Func<string, string?> read) =>
        IsSmoke(filter, read) || read("FILTER") == filter;

    internal static string VerifyHostedCheckout(string filter)
    {
        Assert.True(HasHostedContext(Environment.GetEnvironmentVariable,
            OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, filter),
            "Select the exact authorized hosted filter on its allowed Linux workflow attempt.");

        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!;
        string eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH")!;
        string checkoutSha = File.ReadAllText(Path.Combine(workspace, ".git", "HEAD")).Trim();
        using var dispatch = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.True(dispatch.RootElement.TryGetProperty("inputs", out JsonElement inputs) && MatchesDispatch(inputs, checkoutSha, filter),
            "The dispatch must select the exact Linux leg and filtered unit at a full SHA matching the detached checkout.");
        return checkoutSha;
    }

    internal static bool HasHostedContext(Func<string, string?> read, bool isLinuxX64, string filter = Filter) =>
        isLinuxX64 && read("FILTER") == filter && read("RUNNER_ENVIRONMENT") == "github-hosted" &&
        read("KE_GRAPHICS_BACKEND") == "vulkan-native" &&
        read("GITHUB_WORKFLOW_REF") == "APKiwiOrg/KhaozEngine/.github/workflows/gpu-hosted-bench.yml@refs/heads/main" &&
        (filter != ComparisonFilter || read("GITHUB_EVENT_NAME") == "workflow_dispatch" && read("GITHUB_RUN_ATTEMPT") == "1");

    internal static bool MatchesDispatch(JsonElement inputs, string checkoutSha, string filter = Filter)
    {
        if (checkoutSha.Length != 40 || inputs.ValueKind != JsonValueKind.Object) return false;
        foreach (char digit in checkoutSha)
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return Matches("ref", checkoutSha) && Matches("legs", "linux-vulkan") && Matches("filter", filter) &&
            (filter != ComparisonFilter || Empty("coverage-cost-unit") && Empty("coverage-perf-unit") &&
                Empty("coverage-perf-manifest-sha256") && Empty("coverage-perf-workload-sha") &&
                Empty("renderer-perf-unit") && Empty("renderer-perf-manifest-sha256") && Empty("renderer-perf-workload-sha"));

        bool Matches(string name, string expected) => inputs.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);
        bool Empty(string name) => !inputs.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.String && value.ValueEquals("");
    }
}

internal sealed class ClothPreparationFactAttribute : FactAttribute
{
    public ClothPreparationFactAttribute(string filter = ClothPreparationAdmission.Filter)
    {
        if (!ClothPreparationAdmission.IsSelected(filter, Environment.GetEnvironmentVariable))
            Skip = "Select the exact hosted filter or enable the untimed smoke.";
    }
}
