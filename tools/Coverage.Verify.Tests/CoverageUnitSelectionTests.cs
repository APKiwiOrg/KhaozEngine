using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.CoverageTooling;

public sealed class CoverageUnitSelectionTests
{
    internal const string CostMethod = "Real_body_size_carry_and_scene_overhead_for_1_and_32_actors";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_unit_selection_excludes_unapproved_measurement(bool crlf)
    {
        string cost = Checkout("KhaozEngine.Render.Tests/Gpu/CoverageRecessionCostGpuTests.cs", crlf);
        var selectedCostMethods = Regex.Matches(cost, @"\[CoverageRecessionHardwareFact\]\s*\[Trait\(""Category"", ""Diagnostic""\)\]\s*public void (\w+)\(")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { CostMethod }, selectedCostMethods);
        string gate = Checkout("KhaozEngine.Render.Tests/Gpu/CoverageRecessionHardwareFactAttribute.cs", crlf);
        Assert.Contains("GetEnvironmentVariable(\"KE_GPU_TESTS\") == \"1\"", gate);
        Assert.Contains("SoftwareAdapter", gate);
        Assert.Contains("IsVirtualisedAdapter", gate);
        Assert.Contains("SupportsCompute", gate);
        Assert.Contains("SupportsTimestamps", gate);
        Assert.Contains("SupportsCompletionFences", gate);
        Assert.Matches(@"device\.Backend is not \(GpuBackendKind\.MetalNative or GpuBackendKind\.VulkanNative or GpuBackendKind\.Direct3D11Native\)", gate);
        Assert.Matches(@"device\.Diagnostics\.SoftwareAdapter != false \|\| GpuFactAttribute\.IsVirtualisedAdapter", gate);
        Assert.Matches(@"device\.Capabilities\.SupportsCompute && device\.Capabilities\.SupportsTimestamps && device\.Capabilities\.SupportsCompletionFences", gate);
        string workflow = Checkout(".github/workflows/cross-platform-gpu.yml", crlf);
        Assert.Contains("Category!=Diagnostic", workflow);
        Assert.Contains("FullyQualifiedName~Compute", workflow);
        foreach (string name in new[] { "CoverageRecessionComputeGpuTests", "CoverageRecessionPublicationGpuTests" })
        {
            string facts = Checkout($"KhaozEngine.Render.Tests/Gpu/{name}.cs", crlf);
            Assert.Contains("[GpuFact]", facts);
            Assert.DoesNotContain("HardwareFact", facts);
            Assert.DoesNotContain("\"Diagnostic\"", facts);
        }
        Assert.Contains("Frame_N_reads_the_surface_slot_written_by_frame_N_minus_1",
            Checkout("KhaozEngine.Render.Tests/Gpu/CoverageRecessionComputeGpuTests.cs", crlf));
    }

    internal static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "KhaozEngine.slnx"))) return dir.FullName;
            throw new DirectoryNotFoundException("The tests require their real repository checkout.");
        }
    }

    internal static string Checkout(string path, bool crlf = false)
    {
        string text = File.ReadAllText(Path.Combine(Root, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
        return crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
    }
}
