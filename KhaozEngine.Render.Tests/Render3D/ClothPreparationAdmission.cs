using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal static class ClothPreparationAdmission
{
    internal const string Filter = "FullyQualifiedName=KhaozEngine.Tests.Render3D." +
        nameof(ClothPreparationCostDiagnostics) + "." +
        nameof(ClothPreparationCostDiagnostics.Report_close_cloth_preparation_cost);
    internal static bool IsSmoke => Environment.GetEnvironmentVariable("KE_CLOTH_PREPARATION_SMOKE") == "1";

    internal static string VerifyHostedCheckout()
    {
        Assert.True(HasHostedContext(Environment.GetEnvironmentVariable,
            OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64),
            "Use the exact authorized Linux hosted filter or set KE_CLOTH_PREPARATION_SMOKE=1 for an untimed smoke.");

        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!;
        string eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH")!;
        string checkoutSha = File.ReadAllText(Path.Combine(workspace, ".git", "HEAD")).Trim();
        using var dispatch = JsonDocument.Parse(File.ReadAllText(eventPath));
        Assert.True(dispatch.RootElement.TryGetProperty("inputs", out JsonElement inputs) && MatchesDispatch(inputs, checkoutSha),
            "The dispatch must select the exact Linux leg and filtered fact at a full SHA matching the detached checkout.");
        return checkoutSha;
    }

    internal static bool HasHostedContext(Func<string, string?> read, bool isLinuxX64) =>
        isLinuxX64 && read("FILTER") == Filter && read("RUNNER_ENVIRONMENT") == "github-hosted" &&
        read("KE_GRAPHICS_BACKEND") == "vulkan-native" && read("GITHUB_WORKFLOW_REF") ==
            "APKiwiOrg/KhaozEngine/.github/workflows/gpu-hosted-bench.yml@refs/heads/main";

    internal static bool MatchesDispatch(JsonElement inputs, string checkoutSha)
    {
        if (checkoutSha.Length != 40 || inputs.ValueKind != JsonValueKind.Object) return false;
        foreach (char digit in checkoutSha)
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return Matches("ref", checkoutSha) && Matches("legs", "linux-vulkan") && Matches("filter", Filter);

        bool Matches(string name, string expected) => inputs.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);
    }
}

internal sealed class ClothPreparationFactAttribute : FactAttribute
{
    public ClothPreparationFactAttribute()
    {
        if (!ClothPreparationAdmission.IsSmoke && Environment.GetEnvironmentVariable("FILTER") != ClothPreparationAdmission.Filter)
            Skip = "Select the exact hosted filter or enable the untimed smoke.";
    }
}
