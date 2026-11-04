using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.D3D11;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class D3D11FxcValidationTests
{
    [FoliageDxbcDumpFact]
    public void DumpFoliageOpaqueDisassembly()
    {
        if (KhaozEngineD3D11.IsPlatformSupported) DumpFoliage();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    void DumpFoliage()
    {
        foreach (ShippedGraphicsProgram program in FoliageOpaqueDxbcFixtureTests.Programs)
        {
            _output?.WriteLine($"FOLIAGEDXBCSOURCE|{program.Name}|{TemporalVisibilityFrozenD.Sha256(program.FragmentGlsl)}");
            string listing = Disassemble(program.FragmentGlsl, program.Name).ReplaceLineEndings("\n");
            int index = 0;
            foreach (string line in listing.Split('\n')) _output?.WriteLine($"FOLIAGEDXBC|{program.Name}|{index++}|{line}");
        }
        Assert.Fail("Foliage DXBC dump printed, failing so the hosted runner retains every line.");
    }
}

public sealed class FoliageDxbcDumpFactAttribute : FactAttribute
{
    public FoliageDxbcDumpFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("KE_DUMP_FOLIAGE_DXBC") != "1")
            Skip = "The hosted Windows foliage dump is opt-in.";
    }
}
