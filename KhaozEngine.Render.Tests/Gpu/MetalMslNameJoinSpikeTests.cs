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
    /// three independent ways and only the first is even theoretically repairable by a smarter join rule:
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
    /// THE LEVER 2.2 FORECLOSES WAS MEASURED TOO, so the refusal rests on a number rather than on the argument
    /// alone. With <c>normalizeResourceNames</c> on, all 141 elements get names and 107 of the 159 arguments
    /// join, leaving 52 that do not. So even taking the option the design rules out (it would break the
    /// byte-equality claim the whole no-rebake licence rests on, since the incumbent emits under the library
    /// defaults) the join would be two thirds of a mechanism, which is worse than none: a binding table that is
    /// right most of the time is how the three incidents in 2.2 happened.
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

        /// <summary>One emitted entry-point argument: which Metal index space it landed in, at what index, and
        /// the name the cross-compiler gave it.</summary>
        readonly record struct EmittedArgument(string Space, int Index, string Name);

        [Fact]
        public void EmittedMslArgumentNamesDoNotJoinToReflectionElementNames()
        {
            int programs = 0, elements = 0, namedElements = 0, emitted = 0, exactJoins = 0;
            int namedTexturesAndSamplers = 0;
            var joined = new List<string>();

            void Measure(string program, IEnumerable<ResourceLayoutDescription> layouts,
                (string Stage, string Msl, string Keyword)[] stages)
            {
                programs++;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (ResourceLayoutDescription set in layouts)
                {
                    foreach (ResourceLayoutElementDescription element in set.Elements)
                    {
                        elements++;
                        if (string.IsNullOrEmpty(element.Name)) continue;
                        namedElements++;
                        names.Add(element.Name);
                        if (element.Kind is not (ResourceKind.UniformBuffer
                            or ResourceKind.StructuredBufferReadOnly or ResourceKind.StructuredBufferReadWrite))
                        {
                            namedTexturesAndSamplers++;
                        }
                    }
                }

                foreach ((string stage, string msl, string keyword) in stages)
                {
                    foreach (EmittedArgument argument in ParseEntryPointArguments(msl, keyword))
                    {
                        emitted++;
                        if (!names.Contains(argument.Name)) continue;
                        exactJoins++;
                        joined.Add($"{program}.{stage} [[{argument.Space}({argument.Index})]] '{argument.Name}'");
                    }
                }
            }

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                VertexFragmentCompilationResult result = SpirvCompilation.CompileVertexFragment(
                    SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name),
                    SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name),
                    CrossCompileTarget.MSL, ShippedOptions);
                Measure(program.Name, result.Reflection.ResourceLayouts, new[]
                {
                    ("vertex", result.VertexShader, "vertex "),
                    ("fragment", result.FragmentShader, "fragment "),
                });
            }

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                ComputeCompilationResult result = SpirvCompilation.CompileCompute(
                    SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name),
                    CrossCompileTarget.MSL, ShippedOptions);
                Measure(kernel.Name, result.Reflection.ResourceLayouts,
                    new[] { ("compute", result.ComputeShader, "kernel ") });
            }

            var report = new StringBuilder();
            report.AppendLine($"programs={programs} layoutElements={elements} namedElements={namedElements} "
                + $"namedTexturesAndSamplers={namedTexturesAndSamplers} emittedArguments={emitted} "
                + $"exactNameJoins={exactJoins}");
            foreach (string line in joined) report.AppendLine("  joined: " + line);
            _output.WriteLine(report.ToString());

            // Not vacuous: an emptied catalog would otherwise satisfy every assertion below by having nothing to
            // measure, which is the failure mode the sibling HLSL test records in its own header.
            Assert.True(programs > 30 && elements > 100 && emitted > 100,
                "the shipped-program walk found almost nothing, so the measurement below means nothing:\n" + report);

            // The decisive fact. Every texture and sampler element reflects with an empty name, so for the
            // majority of elements there is no key to join on at all and no join rule can invent one.
            Assert.True(namedTexturesAndSamplers == 0,
                "a texture or sampler element now carries a reflected NAME, which it did not when M-B1's join "
                + "was refuted. That is the missing half of the join arriving, so section 2.2 should be re-read "
                + "and M-B1 reconsidered rather than this number being updated:\n" + report);

            // And the headline. Zero, over every shipped program, under the options the backend will use.
            Assert.True(exactJoins == 0,
                "emitted MSL argument names now JOIN to reflected element names, which they did not when this "
                + "was measured. M-B1's name join was refused on that measurement and the incumbent's "
                + "arithmetic taken instead (#586), so a non-zero count here is the reason to reopen the "
                + "ruling in section 2.2, not a test to update:\n" + report);
        }

        /// <summary>
        /// The cross-compile options the shipped path uses, stated rather than defaulted. They are the library
        /// defaults, which is what <see cref="HlslCrossCompilePin"/> pins for the HLSL target and what the
        /// incumbent's own <c>CreateFromSpirv</c> call passes, and the third value is the one that matters here:
        /// <c>normalizeResourceNames</c> is OFF, which is why resource names arrive stripped to SPIRV-Cross's
        /// own ids. Section 2.2 rules that flipping it is not available as a lever, because any pin that differs
        /// from the incumbent's breaks the byte-equality claim the no-rebake licence rests on.
        /// </summary>
        static CrossCompileOptions ShippedOptions => new(
            HlslCrossCompilePin.FixClipSpaceZ,
            HlslCrossCompilePin.InvertVertexOutputY,
            HlslCrossCompilePin.NormalizeResourceNames);

        /// <summary>
        /// The entry point's resource arguments, in declaration order. The closing parenthesis is matched by
        /// DEPTH rather than taken as the first one, because every argument carries an attribute of its own and
        /// a naive scan stops inside <c>[[buffer(0)]]</c> and sees a single argument. That is the same walk
        /// <c>ShaderValidation.CheckMslBufferSlots</c> already ships, and row 9 promotes into the binding path.
        /// </summary>
        static List<EmittedArgument> ParseEntryPointArguments(string msl, string entryKeyword)
        {
            var arguments = new List<EmittedArgument>();
            int start = msl.IndexOf(entryKeyword, StringComparison.Ordinal);
            if (start < 0) return arguments;
            int open = msl.IndexOf('(', start);
            if (open < 0) return arguments;

            int close = -1, depth = 0;
            for (int i = open; i < msl.Length; i++)
            {
                if (msl[i] == '(') depth++;
                else if (msl[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0) return arguments;

            foreach (string raw in msl.Substring(open + 1, close - open - 1).Split(','))
            {
                string argument = raw.Trim();
                foreach (string space in new[] { "buffer", "texture", "sampler" })
                {
                    string marker = "[[" + space + "(";
                    int at = argument.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;
                    int numberStart = at + marker.Length;
                    int numberEnd = argument.IndexOf(')', numberStart);
                    if (numberEnd < 0) continue;
                    if (!int.TryParse(argument.AsSpan(numberStart, numberEnd - numberStart), out int index)) continue;

                    // The declared name is the last identifier before the attribute, past any reference or
                    // pointer punctuation: "constant _68& _70 [[buffer(0)]]" names _70.
                    string declaration = argument[..at].TrimEnd();
                    int split = declaration.LastIndexOfAny(new[] { ' ', '&', '*' });
                    arguments.Add(new EmittedArgument(space, index,
                        split >= 0 ? declaration[(split + 1)..] : declaration));
                    break;
                }
            }
            return arguments;
        }
    }
}
