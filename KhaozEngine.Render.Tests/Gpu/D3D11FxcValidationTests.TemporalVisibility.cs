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
    /// wherever the fast path runs (<see cref="TemporalVisibilityFastPath.SlowForBinade"/>), and that is all it proves.
    /// So every chain in D's program and the shipped one must head in one of those two orders, subtract one of the
    /// jitter elements D's chains subtract, and continue from the division exactly as one of D's chains does, and the
    /// shipped program must hold D's chains plus the fast path's six. Each check compares the shipped program against
    /// the same program rewritten to D (<see cref="TemporalVisibilityFrozenD.ToD"/>), compiled the same way, so whatever
    /// D's own program declares or computes elsewhere is the baseline. Every program's chains are logged by form, with
    /// any -0.5 position the reader could not read and the instructions behind it. The disassembly is read by
    /// <see cref="TemporalVisibilityDxbc"/>, whose own facts run on every leg, on real FXC text among others.
    /// </summary>
    public sealed partial class D3D11FxcValidationTests
    {
        readonly ITestOutputHelper? _output;

        public D3D11FxcValidationTests(ITestOutputHelper output) => _output = output;

        /// <summary>The second pass at both precisions, upscaling and at the display's own size.</summary>
        static readonly (string Name, string Source)[] AccumulatePrograms =
        [
            (nameof(ShaderSources.TemporalAccumulateFrag), ShaderSources.TemporalAccumulateFrag),
            (nameof(ShaderSources.TemporalAccumulateHalfFrag), ShaderSources.TemporalAccumulateHalfFrag),
            (nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), ShaderSources.TemporalAccumulateAtDisplaySizeFrag),
            (nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag),
                ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag),
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
            // The decision alone, D's slow path beside the fast path: no indexable temp at all, and six chains more
            // than D's helper alone, each in an analysed order with D's remainder.
            string helper = Disassemble(TemporalVisibilityCompiledFormTests.DecisionHelperProgram, "visibility decision");
            string dHelper = Disassemble(TemporalVisibilityCompiledFormTests.DecisionHelperProgram
                .Replace(ShaderSources.TemporalVisibilityGlsl, TemporalVisibilityFrozenD.VisibilityGlsl
                    + DHelperDecision, StringComparison.Ordinal), "visibility decision as D");
            if (TemporalVisibilityDxbc.Declarations(helper).IndexableTemps.Length > 0)
                failures.Add("the decision helper declares an indexable temp: "
                    + string.Join(" ", TemporalVisibilityDxbc.Declarations(helper).IndexableTemps));
            Compare("decision helper", dHelper, helper, failures);
            foreach ((string name, string source) in AccumulatePrograms)
                Compare(name, Disassemble(TemporalVisibilityFrozenD.ToD(source), name + " as D"),
                    Disassemble(source, name), failures);
            Assert.True(failures.Count == 0, "The visibility fast path does not compile to D's DXBC shape:\n  "
                + string.Join("\n  ", failures));
        }

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

        // The shipped program against its D form: the same indexable temps, D's position chains plus the fast path's
        // six, every head in one of the two analysed orders with one of D's jitter elements, and every remainder one
        // of D's.
        void Compare(string name, string d, string shipped, List<string> failures)
        {
            (int dTemps, string[] dIndexable) = TemporalVisibilityDxbc.Declarations(d);
            (int temps, string[] indexable) = TemporalVisibilityDxbc.Declarations(shipped);
            List<string> dChains = TemporalVisibilityDxbc.Chains(d), chains = TemporalVisibilityDxbc.Chains(shipped);
            _output?.WriteLine($"{name}: dcl_temps {temps} (D {dTemps}), indexable temps [{string.Join(" ", indexable)}] "
                + $"(D [{string.Join(" ", dIndexable)}]), {chains.Count} position chain components (D {dChains.Count})");
            Log("D", d, dChains);
            Log("shipped", shipped, chains);
            if (!indexable.SequenceEqual(dIndexable))
                failures.Add($"{name}: indexable temps [{string.Join(" ", indexable)}], D's [{string.Join(" ", dIndexable)}]");
            if (dChains.Count < 2) failures.Add($"{name}: D's DXBC shows {dChains.Count} position chain components, not 2 or more");
            if (chains.Count != dChains.Count + 6)
                failures.Add($"{name}: {chains.Count} position chain components, {dChains.Count} in D plus 6 expected");
            foreach ((string side, List<string> list) in new[] { ("D's", dChains), ("the shipped", chains) })
                foreach (string chain in list.Distinct().Where(c => Is(c, TemporalVisibilityDxbc.HeadForm.Other)))
                    failures.Add($"{name}: {side} program has a position chain whose head is neither analysed order: {chain}");
            var remainders = dChains.Select(TemporalVisibilityDxbc.Remainder).ToHashSet(StringComparer.Ordinal);
            var jitters = dChains.Select(TemporalVisibilityDxbc.Jitter).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (string chain in chains.Distinct())
            {
                if (!remainders.Contains(TemporalVisibilityDxbc.Remainder(chain)))
                    failures.Add($"{name}: a position chain whose remainder no chain of D's has: {chain}");
                if (TemporalVisibilityDxbc.Jitter(chain) is { } jitter && !jitters.Contains(jitter))
                    failures.Add($"{name}: a position chain subtracting {jitter}, which no chain of D's subtracts: {chain}");
            }

            static bool Is(string chain, TemporalVisibilityDxbc.HeadForm form) => TemporalVisibilityDxbc.Form(chain) == form;

            void Log(string side, string disassembly, List<string> list)
            {
                _output?.WriteLine($"  {side}: {list.Count(c => Is(c, TemporalVisibilityDxbc.HeadForm.Written))} written, "
                    + $"{list.Count(c => Is(c, TemporalVisibilityDxbc.HeadForm.Reassociated))} reassociated, "
                    + $"{list.Count(c => Is(c, TemporalVisibilityDxbc.HeadForm.Other))} other");
                foreach (string chain in list.Distinct())
                    _output?.WriteLine($"    {list.Count(c => c == chain)} x {TemporalVisibilityDxbc.Form(chain)}: {chain}");
                foreach (string unread in TemporalVisibilityDxbc.Unread(disassembly).Take(24))
                    _output?.WriteLine($"    unread -0.5 position, {unread}");
            }
        }

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
