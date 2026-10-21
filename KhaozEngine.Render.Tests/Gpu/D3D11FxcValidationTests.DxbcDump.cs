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
    /// prints FXC's full disassembly of the decision helper and of every visibility program, as rewritten to D and as
    /// shipped, one <c>DXBC|program|D or S|line number|text</c> line each, and fails so the runner prints the output.
    /// To recapture, set the variable on the Windows FXC step of <c>cross-platform-gpu.yml</c> in a diagnostic commit
    /// on a diagnostic branch, dispatch the workflow there with <c>legs=direct3d11-native</c>, take each stale fixture's
    /// lines from the job log with carriage returns removed, gzip them over the fixture and store the GLSL hash beside
    /// it. Delete the diagnostic branch afterwards.
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
            Print("DecisionHelper", "D", Disassemble(DHelperProgram, "visibility decision as D"));
            Print("DecisionHelper", "S", Disassemble(TemporalVisibilityCompiledFormTests.DecisionHelperProgram,
                "visibility decision"));
            foreach ((string name, string source) in VisibilityPrograms)
            {
                Print(name, "D", Disassemble(TemporalVisibilityFrozenD.ToD(source), name + " as D"));
                Print(name, "S", Disassemble(source, name));
            }
            Assert.Fail("visibility DXBC dump printed, failing so the runner shows it");
        }

        void Print(string name, string side, string disassembly)
        {
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
