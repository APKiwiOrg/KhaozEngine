using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Counts emitted helper call sites, not dynamic invocations or cost. No native device is involved.</summary>
public sealed class CoverageRecessionHelperInventoryTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Graphics() => CoverageGraphicsProgramTests.Programs();
    public static IEnumerable<object[]> Kernels() => CoverageCarryKernelTests.Stages();

    [Fact]
    public void Helper_inventory_covers_each_coverage_program_and_retained_kernel()
    {
        Assert.Equal(new[] { "cascade", "cascade-complement", "cascade-dissolve", "dissolve", "main", "motion",
            "motion-dissolve", "outline-full", "outline-visible", "point", "point-complement", "point-dissolve" },
            Graphics().Select(p => (string)p[0]).Order());
        Assert.Equal(new[] { "CoverageCarrySkin", "CoverageCarryTransport", "CoverageCarryBasis", "CoverageCarryGather", "CoverageCarryPublish" },
            CoverageCarryShaders.Kernels.Select(k => k.Name));
    }

    [Theory]
    [MemberData(nameof(Graphics))]
    public void Inlining_changes_only_vertex_helper_attributes(string name, string vertex, string fragment)
    {
        var precise = new GpuShaderCompileOptions(PreciseMath: true);
        var shipped = MetalShaderBuild.Pair(vertex, fragment, null, options: precise);
        var inlined = MetalShaderBuild.Pair(vertex, fragment, null, options: precise, inlineHelpers: true);
        var explicitOff = MetalShaderBuild.Pair(vertex, fragment, null, options: precise, inlineHelpers: false);
        string text = shipped.StageOf(MetalShaderStage.Vertex).Msl;
        Assert.Equal(text, explicitOff.StageOf(MetalShaderStage.Vertex).Msl);
        Compare(name, text, inlined.StageOf(MetalShaderStage.Vertex).Msl);
        Assert.Equal(shipped.StageOf(MetalShaderStage.Fragment).Msl, inlined.StageOf(MetalShaderStage.Fragment).Msl);
    }

    [Theory]
    [MemberData(nameof(Kernels))]
    public void Inlining_changes_only_carry_helper_attributes(int stage)
    {
        var kernel = CoverageCarryShaders.Kernels[stage];
        var shipped = MetalShaderBuild.Compute(kernel.Source, null, options: CoverageCarryShaders.Options);
        var inlined = MetalShaderBuild.Compute(kernel.Source, null, options: CoverageCarryShaders.Options, inlineHelpers: true);
        Assert.Equal((shipped.X, shipped.Y, shipped.Z), (inlined.X, inlined.Y, inlined.Z));
        Compare(kernel.Name, shipped.Program.StageOf(MetalShaderStage.Compute).Msl, inlined.Program.StageOf(MetalShaderStage.Compute).Msl);
    }

    void Compare(string name, string shipped, string inlined)
    {
        Assert.DoesNotContain("[[clang::optnone]]", inlined);
        Assert.Equal(shipped.Replace("[[clang::optnone]]", "[[clang::always_inline]]", StringComparison.Ordinal), inlined);
        var originalCalls = Calls(shipped);
        var inlineCalls = Calls(inlined);
        Assert.Equal(originalCalls, inlineCalls);
        output.WriteLine("COVERAGE_PRECISE_HELPER_INVENTORY_JSON=" + JsonSerializer.Serialize(new
        { name, shipped = originalCalls, inlined = inlineCalls }));
    }

    internal sealed record HelperCalls(string Helper, int CallSites);
    static HelperCalls[] Calls(string msl)
    {
        // Count lexical call sites only. Drop each template's declaration and retain calls inside helper bodies.
        var definitions = Regex.Matches(msl, @"\[\[clang::(?:optnone|always_inline)\]\][^\r\n]*\b(spvF\w+)\(")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        return definitions.Select(name => new HelperCalls(name, Regex.Matches(msl, @"\b" + name + @"\s*\(").Count - 1)).ToArray();
    }
}
