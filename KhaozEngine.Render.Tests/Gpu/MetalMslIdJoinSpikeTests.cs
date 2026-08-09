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
    /// THE VARIANT THE NAME SPIKE DID NOT TRY. <see cref="MetalMslNameJoinSpikeTests"/> refuted M-B1's join on
    /// NAMES, and that refutation stands. It never tried the other key. SPIRV-Cross names an unnamed variable
    /// <c>_&lt;id&gt;</c> after its SPIR-V result id, and the <c>DescriptorSet</c> and <c>Binding</c> decorations
    /// that give that id its meaning are not debug information, so they survive the
    /// <c>SpirvFrontEndPin.Debug=false</c> stripping that removes the names. So the join can be keyed on the ID:
    /// walk each STAGE'S OWN module for every <c>(id, set, binding)</c> triple, read the id back out of the
    /// emitted argument name, and resolve <c>(set, binding)</c> against the reflection's layout elements.
    ///
    /// <para>
    /// THE ANSWER IS 159 OF 159, over the same 42 programs and the same 159 arguments the name spike measured,
    /// under the same options. Every emitted entry-point argument resolves to exactly one layout element, with no
    /// failure class of any size. Measured 2026-08-10.
    /// </para>
    /// <para>
    /// WHY IT WORKS WHERE THE NAME JOIN CANNOT, in one sentence each. The name join died on textures and
    /// samplers reflecting with an EMPTY name, and a decoration is present whether or not a name is. It died on
    /// buffer elements being named <c>_68_70</c> for the <c>{blockType}_{instance}</c> pair while the argument is
    /// named <c>_70</c>, and an id needs no such convention. And it died PER STAGE, because each stage renumbers
    /// its ids independently, which is the one that matters here: this join reads each stage's ids out of THAT
    /// STAGE'S module, so independent renumbering is not a hazard, it is the mechanism.
    /// </para>
    /// <para>
    /// FOUR CONTROLS, because a join that reports 100 per cent is exactly where a vacuous measurement hides. The
    /// positional assumption underneath <c>layouts[set].Elements[binding]</c> is CHECKED rather than assumed: no
    /// observed binding lands past its set's element array and no observed set lands past the layout array. The
    /// join is a BIJECTION per stage, so no two arguments resolve to one element. It is not trivially the binding
    /// number, since 80 of the 159 arguments carry a Metal index that differs from their binding. And stages
    /// really do omit elements (95 of 254 stage/element slots are unreferenced), so this is not the degenerate
    /// case where every stage happens to see the whole layout.
    /// </para>
    /// <para>
    /// AND THE NUMBER THAT MATTERS MOST TO THE RE-ADJUDICATION IS THE ONE THAT CAME BACK ZERO. Over the whole
    /// shipped set the id join and the INCUMBENT'S ARITHMETIC produce the same index for all 159 arguments, so
    /// taking this mechanism would change no binding today. The reason is measured rather than assumed: the
    /// incumbent's per-kind counters only go wrong when a stage skips an element of the SAME kind that precedes a
    /// referenced one, and there are ZERO such arguments, even though 95 slots are unreferenced (all of them
    /// cross-kind, which the per-kind counters absorb). That is not evidence the incumbent's arithmetic is sound.
    /// It is evidence that the shipped shaders have already been bent to avoid the shape that breaks it: the
    /// one-UBO constraint in section 2.3 IS that shape, carried as a seam invariant, and the two incidents in 2.2
    /// were fixed by reordering the shader's own reads. So the fallback is safe on today's set, and safe for a
    /// measured reason rather than a hopeful one.
    /// </para>
    /// <para>
    /// WHAT THIS TEST IS FOR, THEREFORE. Not to assert that the id join is unused, but to be the tripwire on the
    /// condition that makes the fallback stop being safe. The moment a shipped shader gains an unreferenced
    /// same-kind element ahead of a referenced one, the incumbent's arithmetic is wrong for that program and this
    /// goes red naming it, which is precisely when
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/586">#586</see>'s fallback needs revisiting and
    /// a mechanism that measures 159 of 159 is sitting here ready. It asserts PROPERTIES rather than the census,
    /// so adding a shader does not make it red for no reason.
    /// </para>
    /// <para>
    /// Recording only, not a re-ruling: section 2.2a carries the measurement, and re-adjudicating M-B1 is its own
    /// step, for whoever takes rows 9, 10 and 13. Device-free and on every leg.
    /// </para>
    /// </summary>
    public sealed class MetalMslIdJoinSpikeTests
    {
        readonly ITestOutputHelper _output;

        public MetalMslIdJoinSpikeTests(ITestOutputHelper output) => _output = output;

        /// <summary>The cross-compile options the shipped path uses, identical to the name spike's, so the two
        /// measurements differ in the JOIN and in nothing else.</summary>
        static CrossCompileOptions ShippedOptions => new(
            HlslCrossCompilePin.FixClipSpaceZ,
            HlslCrossCompilePin.InvertVertexOutputY,
            HlslCrossCompilePin.NormalizeResourceNames);

        /// <summary>Every counter the measurement produces, so the report and the assertions read the same
        /// numbers rather than two independently maintained sets.</summary>
        sealed class Census
        {
            internal int Programs, Arguments, Joins;
            internal int SetOverflow, BindingOverflow, Collisions;
            internal int IndexDiffersFromBinding, IndexDiffersFromIncumbent;
            internal int StageElementSlots, UnreferencedSlots, SameKindGapAhead;
            internal readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);
            internal readonly List<string> Detail = new();

            internal void Fail(string cls, string detail)
            {
                Failures.TryGetValue(cls, out int n);
                Failures[cls] = n + 1;
                Note($"{cls}: {detail}");
            }

            internal void Note(string line)
            {
                if (Detail.Count < 30) Detail.Add(line);
            }

            internal string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"programs={Programs} emittedArguments={Arguments} idJoins={Joins}");
                sb.AppendLine($"set past the layout array={SetOverflow} binding past its element array={BindingOverflow}");
                sb.AppendLine($"collisions (two arguments resolving to one element in one stage)={Collisions}");
                sb.AppendLine($"metal index differs from the binding number={IndexDiffersFromBinding}");
                sb.AppendLine($"stage/element slots={StageElementSlots} unreferenced by their stage={UnreferencedSlots}");
                sb.AppendLine($"arguments with an unreferenced SAME-KIND element ahead of them={SameKindGapAhead}");
                sb.AppendLine($"metal index differs from the incumbent's arithmetic={IndexDiffersFromIncumbent}");
                foreach ((string cls, int n) in Failures.OrderByDescending(p => p.Value))
                    sb.AppendLine($"  FAILURE CLASS x{n}: {cls}");
                foreach (string line in Detail) sb.AppendLine("  " + line);
                return sb.ToString();
            }
        }

        [Fact]
        public void EmittedMslArgumentIdsJoinToEveryReflectedElementThroughTheirDecorations()
        {
            var census = new Census();

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                byte[] vertex = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name);
                byte[] fragment = SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name);
                VertexFragmentCompilationResult result = SpirvCompilation.CompileVertexFragment(
                    vertex, fragment, CrossCompileTarget.MSL, ShippedOptions);
                MeasureProgram(census, program.Name, result.Reflection.ResourceLayouts, new[]
                {
                    ("vertex", result.VertexShader, "vertex ", vertex),
                    ("fragment", result.FragmentShader, "fragment ", fragment),
                });
            }

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                byte[] compute = SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name);
                ComputeCompilationResult result = SpirvCompilation.CompileCompute(
                    compute, CrossCompileTarget.MSL, ShippedOptions);
                MeasureProgram(census, kernel.Name, result.Reflection.ResourceLayouts,
                    new[] { ("compute", result.ComputeShader, "kernel ", compute) });
            }

            string report = census.Report();
            _output.WriteLine(report);

            // Not vacuous: an emptied catalog would otherwise satisfy an "everything joined" assertion by having
            // nothing to join, which is the failure mode the sibling spikes record in their own headers.
            Assert.True(census.Programs > 30 && census.Arguments > 100,
                "the shipped-program walk found almost nothing, so the measurement below means nothing:\n" + report);

            // THE HEADLINE. Every emitted argument resolves to exactly one layout element.
            Assert.True(census.Joins == census.Arguments,
                "the id-keyed join no longer reaches every emitted argument. It measured 159 of 159 when section "
                + "2.2a recorded it, and that number is what M-B1's re-adjudication weighs, so a shortfall here "
                + "changes the adjudication rather than being a test to update:\n" + report);
            Assert.Empty(census.Failures);

            // The positional assumption the join rests on, checked rather than assumed: an element's INDEX within
            // a set is that element's BINDING number.
            Assert.True(census.SetOverflow == 0 && census.BindingOverflow == 0,
                "a decorated (set, binding) landed outside the reflection's layout array, so the positional "
                + "assumption under layouts[set].Elements[binding] does not hold:\n" + report);

            // A bijection per stage, so the join maps each argument to its OWN element rather than collapsing
            // several onto one.
            Assert.True(census.Collisions == 0,
                "two arguments in one stage resolved to the same layout element:\n" + report);

            // POSITIVE CONTROLS. The join is not trivially the binding number, and stages really do omit
            // elements, so neither half of the measurement is a degenerate case.
            Assert.True(census.IndexDiffersFromBinding > 0,
                "every emitted metal index now equals its binding number, which would make this join measure "
                + "nothing that a per-set count does not already give:\n" + report);
            Assert.True(census.UnreferencedSlots > 0,
                "every stage now references every element of its layout, so this census no longer exercises the "
                + "partial-stage case at all:\n" + report);

            // THE TRIPWIRE, and the reason this test is worth keeping. Both numbers are zero today, which is why
            // the fallback in #586 is safe, and either going non-zero is the event that ends that.
            Assert.True(census.SameKindGapAhead == 0 && census.IndexDiffersFromIncumbent == 0,
                "a shipped shader now has an emitted argument with an UNREFERENCED SAME-KIND element ahead of "
                + "it, or an emitted index that disagrees with the incumbent's arithmetic. Those are the same "
                + "event seen twice, and it is the condition under which the fallback taken in #586 (reproduce "
                + "the incumbent's arithmetic, known defect included) binds the WRONG resource for that program. "
                + "The id join measured here reaches every argument and is the mechanism that fixes it, so this "
                + "is the moment to re-read section 2.2a rather than to update a number:\n" + report);
        }

        static void MeasureProgram(Census census, string program, IReadOnlyList<ResourceLayoutDescription> layouts,
            (string Stage, string Msl, string Keyword, byte[] Spirv)[] stages)
        {
            census.Programs++;
            int totalElements = layouts.Sum(l => l.Elements.Length);

            foreach ((string stage, string msl, string keyword, byte[] spirv) in stages)
            {
                IReadOnlyDictionary<uint, SpirvResourceDecoration> decorations =
                    SpirvResourceDecorations.Read(spirv);
                List<EmittedArgument> arguments = MslEntryPointArguments.Parse(msl, keyword);

                census.StageElementSlots += totalElements;
                census.UnreferencedSlots += totalElements - arguments.Count;

                var resolved = new HashSet<(int Set, int Binding)>();
                var placed = new List<(EmittedArgument Argument, int Set, int Binding)>();

                foreach (EmittedArgument argument in arguments)
                {
                    census.Arguments++;
                    string where = $"{program}.{stage} [[{argument.Space}({argument.Index})]] '{argument.Name}'";

                    if (!MslEntryPointArguments.TryReadId(argument.Name, out uint id))
                    { census.Fail("argument name is not an _<id>", where); continue; }
                    if (!decorations.TryGetValue(id, out SpirvResourceDecoration decoration))
                    { census.Fail("id carries no set and binding in this stage's module", where); continue; }
                    if (decoration.Set >= (uint)layouts.Count)
                    {
                        census.SetOverflow++;
                        census.Fail("set past the layout array", $"{where} set={decoration.Set}");
                        continue;
                    }

                    ResourceLayoutElementDescription[] elements = layouts[(int)decoration.Set].Elements;
                    if (decoration.Binding >= (uint)elements.Length)
                    {
                        census.BindingOverflow++;
                        census.Fail("binding past that set's element array",
                            $"{where} set={decoration.Set} binding={decoration.Binding}");
                        continue;
                    }

                    ResourceKind kind = elements[(int)decoration.Binding].Kind;
                    if (!SpaceMatches(argument.Space, kind))
                    {
                        census.Fail("resolved element is the wrong kind for the index space", $"{where} -> {kind}");
                        continue;
                    }

                    census.Joins++;
                    if (argument.Index != (int)decoration.Binding) census.IndexDiffersFromBinding++;
                    if (!resolved.Add(((int)decoration.Set, (int)decoration.Binding))) census.Collisions++;
                    placed.Add((argument, (int)decoration.Set, (int)decoration.Binding));
                }

                CompareAgainstTheIncumbent(census, program, stage, layouts, resolved, placed);
            }
        }

        /// <summary>
        /// The two numbers that decide whether the fallback in #586 is currently safe, computed together because
        /// they are the same event seen from two sides. <paramref name="resolved"/> is what this stage actually
        /// referenced, which is what makes the gap count mean anything.
        /// </summary>
        static void CompareAgainstTheIncumbent(Census census, string program, string stage,
            IReadOnlyList<ResourceLayoutDescription> layouts, HashSet<(int Set, int Binding)> resolved,
            List<(EmittedArgument Argument, int Set, int Binding)> placed)
        {
            // The incumbent's arithmetic, reproduced: per-kind counters, with uniform and both structured kinds
            // sharing a buffer counter and both texture kinds sharing a texture counter, accumulated across the
            // preceding layouts in declaration order. That is MTLResourceLayout plus GetBufferBase.
            var incumbent = new Dictionary<(int, int), (int Buffer, int Texture, int Sampler)>();
            int buffers = 0, textures = 0, samplers = 0;
            for (int set = 0; set < layouts.Count; set++)
            {
                ResourceLayoutElementDescription[] elements = layouts[set].Elements;
                for (int e = 0; e < elements.Length; e++)
                {
                    incumbent[(set, e)] = (buffers, textures, samplers);
                    switch (elements[e].Kind)
                    {
                        case ResourceKind.UniformBuffer:
                        case ResourceKind.StructuredBufferReadOnly:
                        case ResourceKind.StructuredBufferReadWrite: buffers++; break;
                        case ResourceKind.TextureReadOnly:
                        case ResourceKind.TextureReadWrite: textures++; break;
                        case ResourceKind.Sampler: samplers++; break;
                    }
                }
            }

            foreach ((EmittedArgument argument, int argSet, int argBinding) in placed)
            {
                (int b, int t, int s) = incumbent[(argSet, argBinding)];
                int incumbentIndex = argument.Space switch { "buffer" => b, "texture" => t, _ => s };
                if (argument.Index != incumbentIndex)
                {
                    census.IndexDiffersFromIncumbent++;
                    census.Note($"DISAGREES {program}.{stage} [[{argument.Space}({argument.Index})]] "
                        + $"set={argSet} binding={argBinding} incumbentWouldSay={incumbentIndex}");
                }

                // The CONDITION behind that disagreement, counted directly so a red run names the cause rather
                // than only the symptom: an element of the SAME kind, ahead of this one in declaration order,
                // that this stage does not reference. The incumbent counts it, the emission does not.
                int gaps = 0;
                for (int set = 0; set <= argSet; set++)
                {
                    ResourceLayoutElementDescription[] elements = layouts[set].Elements;
                    for (int e = 0; e < elements.Length; e++)
                    {
                        if (set == argSet && e >= argBinding) break;
                        if (!SpaceMatches(argument.Space, elements[e].Kind)) continue;
                        if (!resolved.Contains((set, e))) gaps++;
                    }
                }
                if (gaps > 0)
                {
                    census.SameKindGapAhead++;
                    census.Note($"SAME-KIND GAP {program}.{stage} [[{argument.Space}({argument.Index})]] "
                        + $"set={argSet} binding={argBinding} unreferencedSameKindAhead={gaps}");
                }
            }
        }

        static bool SpaceMatches(string space, ResourceKind kind) => space switch
        {
            "buffer" => kind is ResourceKind.UniformBuffer or ResourceKind.StructuredBufferReadOnly
                or ResourceKind.StructuredBufferReadWrite,
            "texture" => kind is ResourceKind.TextureReadOnly or ResourceKind.TextureReadWrite,
            "sampler" => kind is ResourceKind.Sampler,
            _ => false,
        };
    }
}
