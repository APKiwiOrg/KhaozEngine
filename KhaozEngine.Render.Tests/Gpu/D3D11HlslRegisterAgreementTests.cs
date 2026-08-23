using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE TEST ROW 8 DID NOT HAVE, and the one that would have caught the toolchain swap turning the Windows leg
    /// black before a runner did. Decision S2 says the emitted HLSL numbers its own registers and the CPU side
    /// has to agree exactly, and <c>D3D11RegisterNumberingTests</c> pins the CPU half against a transcribed
    /// table. Nothing pinned the OTHER half. So when the swap moved the emission from a per-file counter to the
    /// module's raw <c>Binding</c> decoration, both halves stayed internally consistent, every shipped shader
    /// still compiled, and 165 pixel assertions went red at once with no compile error anywhere.
    /// <para>
    /// WHAT IT ASSERTS. For every shipped program, the registers the emitted HLSL actually names are exactly the
    /// registers <see cref="D3D11RegisterScheme"/> assigns for that program's reflected layouts. Two independent
    /// derivations of one numbering, compared over the real shipped set rather than a sample:
    /// <see cref="HlslRegisterRemap"/> walks the module's resources in <c>(set, binding)</c> order, and the
    /// register scheme walks the reflected layout array in set and declaration order. They agree or the pixels
    /// are wrong.
    /// </para>
    /// <para>
    /// IT READS THE EMITTED TEXT, deliberately, rather than asserting the remap against itself. The failure being
    /// guarded is precisely that SPIRV-Cross ignores what the engine computed, which no assertion over engine
    /// types can see. A regex over <c>register(xN)</c> is crude and is the point: it is what FXC will read.
    /// </para>
    /// <para>
    /// A UNION OVER BOTH STAGES, because each stage emits only the resources it references while the layouts
    /// describe the whole program. <c>Water</c>'s vertex stage names five of the program's seven registers and
    /// its fragment stage all seven, so neither stage alone equals the scheme's set and the union does.
    /// </para>
    /// <para>
    /// DEVICE-FREE, like both halves it compares. The SPIRV-Cross native ships per RID, so this is a plain
    /// <c>[Fact]</c> on every leg rather than a <c>[GpuFact]</c> behind a device.
    /// </para>
    /// </summary>
    public sealed class D3D11HlslRegisterAgreementTests
    {
        [Fact]
        public void EveryShippedGraphicsProgram_EmitsTheRegistersTheRegisterSchemeAssigns()
        {
            var problems = new List<string>();
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                    program.VertexGlsl, program.FragmentGlsl, program.Name);
                Compare(program.Name, pair.Reflection, problems, pair.VertexSource, pair.FragmentSource);
            }

            Assert.True(problems.Count == 0, Report(problems));
        }

        [Fact]
        public void EveryShippedComputeKernel_EmitsTheRegistersTheRegisterSchemeAssigns()
        {
            var problems = new List<string>();
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                CrossCompiledCompute compute = SpirvCrossCompile.GlslComputeToHlsl(kernel.ComputeGlsl, kernel.Name);
                Compare(kernel.Name, compute.Reflection, problems, compute.ComputeSource);
            }

            Assert.True(problems.Count == 0, Report(problems));
        }

        static void Compare(string name, ShaderReflection reflection, List<string> problems, params string[] stages)
        {
            var emitted = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string stage in stages)
                foreach (Match match in RegisterPattern.Matches(stage))
                    emitted.Add(match.Groups[1].Value);

            SortedSet<string> assigned = SchemeRegisters(reflection);
            if (emitted.SetEquals(assigned)) return;

            problems.Add($"  {name}:\n"
                + $"    emitted   {string.Join(" ", emitted)}\n"
                + $"    assigned  {string.Join(" ", assigned)}");
        }

        // Every register the CPU side would bind for this program, through the real numbering function. The
        // layouts arrive in set order and the scheme's per-set base is the running total of the earlier ones,
        // which is the same accumulation a pipeline does over its ResourceLayouts array.
        static SortedSet<string> SchemeRegisters(ShaderReflection reflection)
        {
            var registers = new SortedSet<string>(StringComparer.Ordinal);
            var running = new D3D11RegisterCounts(0, 0, 0, 0);
            foreach (GpuResourceLayoutDescription layout in reflection.ResourceLayouts)
            {
                var slots = new D3D11RegisterSlot[layout.Elements.Length];
                D3D11RegisterCounts counts = D3D11RegisterScheme.AssignWithinLayout(layout.Elements, slots);
                foreach (D3D11RegisterSlot slot in slots)
                    registers.Add(D3D11RegisterScheme.Absolute(running, slot).ToString());
                running = running.Plus(counts);
            }

            return registers;
        }

        static string Report(List<string> problems) =>
            "The emitted HLSL names registers the Direct3D 11 register scheme does not assign, or misses ones it "
            + "does. Every shader still compiles and every pixel is wrong, so this is the failure the golden "
            + "suite reports as a black frame on the Windows leg alone. Either HlslRegisterRemap stopped "
            + "installing the numbering, or a SPIRV-Cross upgrade stopped consulting it.\n"
            + string.Join("\n", problems);

        static readonly Regex RegisterPattern =
            new(@"register\(([btsu]\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
