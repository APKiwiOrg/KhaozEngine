using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Vortice.D3DCompiler;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE VISIBILITY FAST PATH IN FXC'S OUTPUT (TAA shadow visibility plan, Task 7f.2, run on the Windows FXC step and
    /// in the 7f.5 hosted selection). The fast path is D's arithmetic in straight-line code, and on Direct3D 11 its bit
    /// identity rests on two things only FXC's own DXBC shows. First, no indexable temp: the fast path names every block
    /// texel as its own value, so FXC must not have placed one in an indexed array. Second, the position chains. FXC at
    /// the shipped flags compiles a chain's head as written, <c>(at + 0.5) - J</c>, in some places and reassociated,
    /// <c>(at - J) + 0.5</c>, in others (hosted run 39362804399). The binade rule makes the two give the same bits
    /// wherever the fast path runs (<see cref="TemporalVisibilityFastPath.SlowForBinade"/>), and every binade block
    /// runs D's function inside the shipped program instead. So D's chains, inside D's loops, must compile in the
    /// shipped program exactly as in D's: the shipped in-loop chains, by head form, head precise markings, jitter
    /// element and remainder from the division on, are D's chain for chain. The fast path adds exactly six
    /// straight-line chains, three per jitter element, each in one of the two analysed orders and with the head
    /// markings, jitter element and remainder of one of D's chains together
    /// (<see cref="TemporalVisibilityDxbcComparison"/>). Each check compares the shipped program against the same
    /// program rewritten to D (<see cref="TemporalVisibilityFrozenD.ToD"/>), compiled the same way, so whatever D's own
    /// program declares or computes elsewhere is the baseline. It covers the decision helper, the four accumulate
    /// programs of the split's second pass and, since review 7f r3 (m-1), the fused TemporalResolve and
    /// TemporalResolveHalf programs, which carry the decision wherever the fused entry runs. Every program's chains are
    /// logged by loop and form, with any -0.5 position the reader could not read and the instructions behind it. The
    /// disassembly is read by <see cref="TemporalVisibilityDxbc"/>, whose own facts run on every leg, on the full
    /// programs as hosted run 39374926165 printed them among others.
    /// </summary>
    public sealed partial class D3D11FxcValidationTests
    {
        readonly ITestOutputHelper? _output;

        public D3D11FxcValidationTests(ITestOutputHelper output) => _output = output;

        /// <summary>Every program that carries the decision: the second pass at both precisions, upscaling and at the
        /// display's own size, and the fused resolve at both precisions.</summary>
        internal static readonly (string Name, string Source)[] VisibilityPrograms =
        [
            (nameof(ShaderSources.TemporalAccumulateFrag), ShaderSources.TemporalAccumulateFrag),
            (nameof(ShaderSources.TemporalAccumulateHalfFrag), ShaderSources.TemporalAccumulateHalfFrag),
            (nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), ShaderSources.TemporalAccumulateAtDisplaySizeFrag),
            (nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag),
                ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag),
            (nameof(ShaderSources.TemporalResolveFrag), ShaderSources.TemporalResolveFrag),
            (nameof(ShaderSources.TemporalResolveHalfFrag), ShaderSources.TemporalResolveHalfFrag),
        ];

        [Fact]
        public void TheVisibilityFastPath_AddsNoIndexableTempAndKeepsDsPositionChain()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) return;   // no d3dcompiler off Windows
            CheckVisibilityFastPath();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void CheckVisibilityFastPath()
        {
            var failures = new List<string>();
            // The decision alone, D's slow path beside the fast path: no indexable temp at all, D's chains in D's loops
            // as D's helper compiles them, and the fast path's six.
            string helper = Disassemble(TemporalVisibilityCompiledFormTests.DecisionHelperProgram, "visibility decision");
            string dHelper = Disassemble(DHelperProgram, "visibility decision as D");
            if (TemporalVisibilityDxbc.Declarations(helper).IndexableTemps.Length > 0)
                failures.Add("the decision helper declares an indexable temp: "
                    + string.Join(" ", TemporalVisibilityDxbc.Declarations(helper).IndexableTemps));
            failures.AddRange(TemporalVisibilityDxbcComparison.Compare("decision helper", dHelper, helper, Log));
            foreach ((string name, string source) in VisibilityPrograms)
                failures.AddRange(TemporalVisibilityDxbcComparison.Compare(name,
                    Disassemble(TemporalVisibilityFrozenD.ToD(source), name + " as D"), Disassemble(source, name), Log));
            Assert.True(failures.Count == 0, "The visibility fast path does not compile to D's DXBC shape:\n  "
                + string.Join("\n  ", failures));

            void Log(string line) => _output?.WriteLine(line);
        }

        /// <summary>The decision helper with its decision replaced by one that only calls D.</summary>
        internal static string DHelperProgram => TemporalVisibilityFrozenDLaterChanges.Restore(
            TemporalVisibilityCompiledFormTests.DecisionHelperProgram
                .Replace(ShaderSources.TemporalVisibilityGlsl, TemporalVisibilityFrozenD.VisibilityGlsl + DHelperDecision,
                    StringComparison.Ordinal), TemporalVisibilityFrozenDLaterChanges.PrecisionFloorTuning);

        // A decision that only calls D, so D's helper compiles with the same main as the shipped one.
        const string DHelperDecision = @"
const uint VisibilityFastPath = 1u, VisibilitySlowPath = 2u, VisibilityRangeSkip = 4u, VisibilityChangeComputed = 8u;
bool temporalVisibilityReleases(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv, float expectedDepth,
    uint currentMask, out float signedChange, out uint path) {
    bool valid = temporalVisibilityChange(uv, centre, last, previousUv, expectedDepth, signedChange);
    path = VisibilitySlowPath | (valid ? VisibilityChangeComputed : 0u);
    return valid && abs(signedChange) > VisibilityReleaseThreshold;
}
";

        [SupportedOSPlatform("windows")]
        static string Disassemble(string fragment, string label)
        {
            CrossCompiledPair hlsl = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert, fragment, label);
            byte[] dxbc = D3D11Fxc.Compile(hlsl.FragmentSource, D3D11ShaderProfile.Fragment, D3D11ShaderDebug.Optimized,
                label);
            GCHandle pin = GCHandle.Alloc(dxbc, GCHandleType.Pinned);
            try
            {
                using var disassembly = Compiler.Disassemble(pin.AddrOfPinnedObject(), dxbc.Length, DisasmFlags.None, null!);
                return disassembly.AsString();
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
