using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Veldrid;
using Veldrid.SPIRV;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// VERIFICATION TASK TWO of row 1 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, and the
    /// one the design calls the gate on its largest risk. Section 2.2 rules that the native Metal backend reads
    /// its binding indices OUT of the emitted MSL (M-B1), joining each entry point's
    /// <c>[[buffer(n)]]</c>, <c>[[texture(n)]]</c> and <c>[[sampler(n)]]</c> arguments to
    /// <c>ShaderReflection</c>'s layout elements BY NAME. It also says, in as many words, that the join is
    /// decided at this spike rather than assumed, and names the fallback if it does not hold.
    ///
    /// <para>
    /// THE ANSWER IS NO, AND IT IS NOT CLOSE. Over every shipped program, under the cross-compile options the
    /// backend will actually use, ZERO emitted argument names match any reflected element name. The join fails
    /// three independent ways and only the second is even theoretically repairable by a smarter join rule:
    /// </para>
    /// <list type="number">
    /// <item><description>EVERY texture and sampler element reflects with an EMPTY name. There is nothing to
    /// join to, for the majority of the elements, and no rule can fix an absent key. This is the decisive one.
    /// </description></item>
    /// <item><description>Buffer elements DO carry a name, but it is SPIRV-Cross's
    /// <c>{blockType}_{instance}</c> pair (<c>_68_70</c>) while the emitted argument is named for the instance
    /// alone (<c>_70</c>). A suffix rule would join those, at the cost of depending on a naming convention
    /// nothing promises.</description></item>
    /// <item><description>Even that fails per STAGE. The reflection is computed once for the pair, but each
    /// stage's emission renumbers ids independently, so <c>Model</c>'s vertex stage emits <c>_70</c> where its
    /// fragment stage emits <c>_77</c> for the same layout element, and only one of the two can ever suffix-match
    /// the single reflected name.</description></item>
    /// </list>
    /// <para>
    /// THE NUMBERS, measured 2026-08-10 over 42 shipped programs (34 graphics pairs plus the compute kernels at
    /// all four cascade resolutions). 141 layout elements, of which 58 are buffers and carry a name and 83 are
    /// textures or samplers and do not. 159 emitted entry-point arguments. Exact name matches: 0. Under a suffix
    /// rule: 58, all of them the vertex or compute stage's buffer argument. Arguments with no named element of
    /// any kind to join to: 91. Buffer arguments in a second stage whose id appears nowhere in the reflection:
    /// 10.
    /// </para>
    /// <para>
    /// EVERY ONE OF THOSE ROWS IS NOW MEASURED HERE rather than recorded in prose beside a test that checked two
    /// of them. The census is reported on every run and its SHAPE is asserted, and the suffix count is a POSITIVE
    /// CONTROL: at least one buffer argument does suffix-match a reflected element name, so a parse that had
    /// silently stopped returning arguments could not satisfy the two negative assertions vacuously.
    /// </para>
    /// <para>
    /// THE LEVER 2.2 FORECLOSES IS MEASURED HERE TOO, in the same run rather than in prose, so the refusal rests
    /// on a number rather than on the argument alone. With <c>normalizeResourceNames</c> ON, all 141 elements get
    /// names and 107 of the 159 arguments join, leaving 52 that do not. That count is REPORTED and its shape is
    /// asserted (strictly more than the exact-match count, strictly fewer than every argument), because the exact
    /// number moves with the shader set while "materially incomplete" is the property that matters. So even
    /// taking the option the design rules out (it would break the byte-equality claim the whole no-rebake licence
    /// rests on, since the incumbent emits under the library defaults) the join would be two thirds of a
    /// mechanism, which is worse than none: a binding table that is right most of the time is how the three
    /// incidents in 2.2 happened.
    /// </para>
    /// <para>
    /// SO THE FALLBACK 2.2 NAMES IS THE ONE THAT APPLIES: reproduce the incumbent's arithmetic exactly, known
    /// defect included, ship the index-table test (M-T3) as a DETECTOR rather than as an assertion, and file the
    /// numbering fix behind a real SPIRV-Cross binding with 2.2 as its argument. That is filed as
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/586">#586</see>, and the reopening trigger 2.2
    /// already names is <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/462">#462</see>: once
    /// <c>add_msl_resource_binding</c> is reachable the table is AUTHORED rather than read and this whole
    /// question goes away.
    /// </para>
    /// <para>
    /// WHAT THIS TEST DOES NOT SETTLE, and the reader should go and read next. It refutes the join on NAMES. A
    /// join keyed on the SPIR-V ID is a different mechanism and it measures 159 of 159, in
    /// <see cref="MetalMslIdJoinSpikeTests"/> and in section 2.2a.
    /// </para>
    /// <para>
    /// WHY THIS TEST STAYS RATHER THAN BEING DELETED WITH ITS ANSWER. It is a tripwire on the premise. If a
    /// future SPIRV-Cross starts naming resources, or the engine's pinned options move, the count of exact joins
    /// stops being zero and this goes red pointing at 2.2, which is precisely when M-B1 should be reconsidered.
    /// A measurement recorded only in prose could not do that. It asserts the PROPERTY rather than the census,
    /// so adding a shader does not make it red for no reason.
    /// </para>
    /// <para>
    /// Device-free and on every leg, deliberately not named "Golden": the golden filter is for device-backed
    /// pixel comparisons and this needs to run on the legs that have no device at all.
    /// </para>
    /// </summary>
    public sealed class MetalMslNameJoinSpikeTests
    {
        readonly ITestOutputHelper _output;

        public MetalMslNameJoinSpikeTests(ITestOutputHelper output) => _output = output;

        /// <summary>Every row of the census section 2.2a records, counted rather than described.</summary>
        sealed class NameJoinCensus
        {
            internal int Programs, Elements, NamedElements, NamedBufferElements, EmptyNamedElements;
            internal int NamedTexturesAndSamplers;
            internal int Arguments, BufferArguments, ExactJoins, SuffixJoins, SecondStageBufferArgumentsAbsent;
            internal readonly List<string> Joined = new();

            internal string Report(string label)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{label}] programs={Programs} layoutElements={Elements} namedElements={NamedElements}");
                sb.AppendLine($"[{label}] namedBufferElements={NamedBufferElements} emptyNamedElements={EmptyNamedElements} "
                    + $"namedTexturesAndSamplers={NamedTexturesAndSamplers}");
                sb.AppendLine($"[{label}] emittedArguments={Arguments} bufferArguments={BufferArguments} "
                    + $"argumentsWithNoNamedElementToJoinTo={Arguments - BufferArguments}");
                sb.AppendLine($"[{label}] exactNameJoins={ExactJoins} suffixRuleJoins={SuffixJoins} "
                    + $"secondStageBufferArgumentsAbsentFromTheReflection={SecondStageBufferArgumentsAbsent}");
                foreach (string line in Joined.Take(20)) sb.AppendLine("  joined: " + line);
                return sb.ToString();
            }
        }

        [Fact]
        public void EmittedMslArgumentNamesDoNotJoinToReflectionElementNames()
        {
            NameJoinCensus off = Measure(HlslCrossCompilePin.NormalizeResourceNames);
            NameJoinCensus on = Measure(normalizeResourceNames: true);

            string report = off.Report("shipped options") + on.Report("normalizeResourceNames ON");
            _output.WriteLine(report);

            // Not vacuous: an emptied catalog would otherwise satisfy every assertion below by having nothing to
            // measure, which is the failure mode the sibling HLSL test records in its own header.
            Assert.True(off.Programs > 30 && off.Elements > 100 && off.Arguments > 100,
                "the shipped-program walk found almost nothing, so the measurement below means nothing:\n" + report);

            // THE POSITIVE CONTROL, and it is load-bearing for everything under it. The two headline assertions
            // are both NEGATIVE, so a parse that had quietly stopped recognising arguments, or a name comparison
            // that never matched anything, would satisfy them while measuring nothing at all. At least one buffer
            // argument DOES suffix-match a reflected element name, which is the 58-join class, so the join
            // machinery is demonstrably capable of reporting a match on this data.
            Assert.True(off.SuffixJoins > 0,
                "no emitted buffer argument suffix-matches any reflected element name any more. That is the one "
                + "class that DID join, so losing it means the parse or the comparison is broken and the zero "
                + "counts below prove nothing:\n" + report);

            // The census rows section 2.2a records, each asserted on its SHAPE so a new shader moves the number
            // without going red. Named elements are exactly the buffers, unnamed exactly the textures and
            // samplers, and the third failure mode (a second stage whose ids are absent from the reflection) is
            // present rather than theoretical.
            Assert.True(off.NamedBufferElements == off.NamedElements && off.NamedBufferElements > 0,
                "the reflected element names are no longer exactly the buffer elements:\n" + report);
            Assert.True(off.EmptyNamedElements > 0 && off.EmptyNamedElements == off.Elements - off.NamedElements,
                "the empty-named element count no longer accounts for every unnamed element:\n" + report);
            Assert.True(off.SecondStageBufferArgumentsAbsent > 0,
                "no second-stage buffer argument is absent from the reflection any more. That was the third and "
                + "least repairable failure mode of the suffix rule, so its disappearance means the per-stage id "
                + "renumbering has changed and section 2.2 should be re-read:\n" + report);

            // The decisive fact. Every texture and sampler element reflects with an empty name, so for the
            // majority of elements there is no key to join on at all and no join rule can invent one.
            Assert.True(off.NamedTexturesAndSamplers == 0,
                "a texture or sampler element now carries a reflected NAME, which it did not when M-B1's join "
                + "was refuted. That is the missing half of the join arriving, so section 2.2 should be re-read "
                + "and M-B1 reconsidered rather than this number being updated:\n" + report);

            // And the headline. Zero, over every shipped program, under the options the backend will use.
            Assert.True(off.ExactJoins == 0,
                "emitted MSL argument names now JOIN to reflected element names, which they did not when this "
                + "was measured. M-B1's name join was refused on that measurement and the incumbent's "
                + "arithmetic taken instead (#586), so a non-zero count here is the reason to reopen the "
                + "ruling in section 2.2, not a test to update:\n" + report);

            // THE FORECLOSED LEVER, measured rather than described. Flipping normalizeResourceNames names every
            // element and buys a materially INCOMPLETE join: strictly better than the zero above, strictly worse
            // than every argument. The exact number (107 of 159 when this was recorded) is in the report, and the
            // assertion is on the property, because a shader edit moves the count while "two thirds of a
            // mechanism" is what the refusal actually rests on.
            Assert.True(on.ExactJoins > off.ExactJoins,
                "normalizeResourceNames no longer buys any joins at all, so the lever section 2.2 forecloses is "
                + "not the lever it was measured to be:\n" + report);
            Assert.True(on.ExactJoins < on.Arguments,
                "normalizeResourceNames now joins EVERY emitted argument. The refusal in section 2.2 rests on "
                + "that join being materially incomplete (107 of 159 when measured), so a complete one is a "
                + "reason to re-read the ruling, not a test to update. Note it would still break the "
                + "byte-equality claim the no-rebake licence rests on:\n" + report);
        }

        static NameJoinCensus Measure(bool normalizeResourceNames)
        {
            var census = new NameJoinCensus();
            var options = new CrossCompileOptions(
                HlslCrossCompilePin.FixClipSpaceZ, HlslCrossCompilePin.InvertVertexOutputY, normalizeResourceNames);

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                VertexFragmentCompilationResult result = SpirvCompilation.CompileVertexFragment(
                    SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name),
                    SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name),
                    CrossCompileTarget.MSL, options);
                MeasureProgram(census, program.Name, result.Reflection.ResourceLayouts, new[]
                {
                    ("vertex", result.VertexShader, "vertex "),
                    ("fragment", result.FragmentShader, "fragment "),
                });
            }

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                ComputeCompilationResult result = SpirvCompilation.CompileCompute(
                    SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name),
                    CrossCompileTarget.MSL, options);
                MeasureProgram(census, kernel.Name, result.Reflection.ResourceLayouts,
                    new[] { ("compute", result.ComputeShader, "kernel ") });
            }

            return census;
        }

        static void MeasureProgram(NameJoinCensus census, string program,
            IEnumerable<ResourceLayoutDescription> layouts, (string Stage, string Msl, string Keyword)[] stages)
        {
            census.Programs++;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResourceLayoutDescription set in layouts)
            {
                foreach (ResourceLayoutElementDescription element in set.Elements)
                {
                    census.Elements++;
                    bool isBuffer = element.Kind is ResourceKind.UniformBuffer
                        or ResourceKind.StructuredBufferReadOnly or ResourceKind.StructuredBufferReadWrite;
                    if (string.IsNullOrEmpty(element.Name)) { census.EmptyNamedElements++; continue; }
                    census.NamedElements++;
                    names.Add(element.Name);
                    if (isBuffer) census.NamedBufferElements++; else census.NamedTexturesAndSamplers++;
                }
            }

            for (int s = 0; s < stages.Length; s++)
            {
                (string stage, string msl, string keyword) = stages[s];
                foreach (EmittedArgument argument in MslEntryPointArguments.Parse(msl, keyword))
                {
                    census.Arguments++;
                    if (argument.Space != "buffer")
                    {
                        // Every texture and sampler element reflects unnamed under the shipped options, so these
                        // arguments have no named element of any kind to join to. That is the 91 class.
                        if (names.Contains(argument.Name)) { census.ExactJoins++; census.Joined.Add(Where()); }
                        continue;
                    }

                    census.BufferArguments++;
                    if (names.Contains(argument.Name)) { census.ExactJoins++; census.Joined.Add(Where()); }

                    // The suffix rule 2.2 weighs and declines: the element is named for the
                    // {blockType}_{instance} pair while the argument is named for the instance alone.
                    bool suffix = names.Any(n => n.EndsWith(argument.Name, StringComparison.Ordinal));
                    if (suffix) census.SuffixJoins++;
                    else if (s > 0) census.SecondStageBufferArgumentsAbsent++;

                    string Where() => $"{program}.{stage} [[{argument.Space}({argument.Index})]] '{argument.Name}'";
                }
            }
        }
    }
}
