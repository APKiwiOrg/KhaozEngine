using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Vortice.D3DCompiler;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Diagnostic evidence only. The following fix removes this source after the hosted compiler comparison.
public sealed class TemporalVisibilityFxcDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void ReportComparisonInstructions()
    {
        if (!OperatingSystem.IsWindows()) return;
        ReportWindows();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    void ReportWindows()
    {
        CrossCompiledPair hlsl = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert,
            TemporalVisibilityProbe.FragmentSource, "visibility diagnostic");
        output.WriteLine("HLSL_BEGIN\n" + hlsl.FragmentSource + "\nHLSL_END");
        foreach (uint flags in new[] { D3D11ShaderDebug.Optimized, D3D11ShaderDebug.DebugBuild,
            D3D11ShaderDebug.Optimized | (uint)ShaderFlags.IeeeStrictness })
        {
            byte[] dxbc = D3D11Fxc.Compile(hlsl.FragmentSource, "ps_5_0", flags, "visibility diagnostic");
            GCHandle pin = GCHandle.Alloc(dxbc, GCHandleType.Pinned);
            try
            {
                using var disassembly = Compiler.Disassemble(pin.AddrOfPinnedObject(), dxbc.Length,
                    DisasmFlags.None, null!);
                output.WriteLine($"DXBC_BEGIN flags={flags}\n{disassembly.AsString()}\nDXBC_END");
            }
            finally { pin.Free(); }
        }
    }
}
