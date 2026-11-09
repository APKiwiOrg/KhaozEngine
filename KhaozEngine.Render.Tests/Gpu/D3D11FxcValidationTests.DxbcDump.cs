using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.D3D11;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The recapture source of the visibility DXBC fixtures (<see cref="TemporalVisibilityDxbcRealProgramTests"/>).
    /// Skipped unless <see cref="VisibilityDxbcDumpFactAttribute.EnvironmentVariable"/> is <c>1</c> on Windows. Then it
    /// prints FXC's full disassembly of the decision helper and six paired programs as D and shipped, plus every
    /// additional shipped release-bearing program. Each listing is one <c>DXBC|program|D or S|line number|text</c> line
    /// per disassembly line, after a <c>DXBCSOURCE|program|D or S|sha256</c> line holding the hash of the GLSL it
    /// compiled. Then it fails so the runner prints the output.
    /// To recapture, dispatch <c>cross-platform-gpu.yml</c> on the branch with <c>legs=direct3d11-native</c> and
    /// <c>dumpDxbc=true</c>, which arms the variable on the Windows FXC step. Then run
    /// <c>scripts/recapture-taa-dxbc.py</c> with the run id to list the fixtures that would change, and again with
    /// <c>--write</c> to rewrite them and their hashes.
    /// </summary>
    public sealed partial class D3D11FxcValidationTests
    {
        [VisibilityDxbcDumpFact]
        public void DumpVisibilityDisassembly()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) return;
            Dump();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void Dump()
        {
            Print("DecisionHelper", "D", DHelperProgram);
            Print("DecisionHelper", "S", TemporalVisibilityCompiledFormTests.DecisionHelperProgram);
            foreach ((string name, string source) in VisibilityPrograms)
            {
                Print(name, "D", TemporalVisibilityFrozenD.ToD(source));
                Print(name, "S", source);
            }
            foreach ((string name, string source) in TemporalLockReleaseDxbcPrograms.Additional)
                Print(name, "S", source);
            Assert.Fail("visibility DXBC dump printed, failing so the runner shows it");
        }

        [SupportedOSPlatform("windows")]
        void Print(string name, string side, string source)
        {
            _output?.WriteLine($"DXBCSOURCE|{name}|{side}|{TemporalVisibilityFrozenD.Sha256(source)}");
            string disassembly = Disassemble(source, name + " " + side);
            int n = 0;
            foreach (string line in disassembly.Split('\n'))
                _output?.WriteLine($"DXBC|{name}|{side}|{n++}|{line.TrimEnd('\r')}");
        }
    }

    /// <summary>A <see cref="FactAttribute"/> skipped unless <see cref="EnvironmentVariable"/> is <c>1</c> on
    /// Windows.</summary>
    public sealed class VisibilityDxbcDumpFactAttribute : FactAttribute
    {
        /// <summary>The variable that arms the dump.</summary>
        public const string EnvironmentVariable = "KE_DUMP_VISIBILITY_DXBC";

        /// <summary>Skips off Windows, or unless <see cref="EnvironmentVariable"/> is <c>1</c>.</summary>
        public VisibilityDxbcDumpFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "the visibility DXBC dump needs FXC, which runs on Windows only";
            else if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
                Skip = "set " + EnvironmentVariable + "=1 to print the visibility DXBC";
        }
    }
}
