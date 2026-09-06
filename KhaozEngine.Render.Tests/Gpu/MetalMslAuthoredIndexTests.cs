using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE GATE OF ROW 10 (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/693">#693</see>): <b>every
    /// shipped program is bound at the indices the ENGINE authored</b>, checked against the emitted MSL itself
    /// rather than against a second CPU derivation of the same rule.
    ///
    /// <para>
    /// THIS FILE IS <c>MetalMslIdJoinSpikeTests</c>, REPOINTED RATHER THAN RETIRED, and the repoint is the whole
    /// argument. The spike existed to measure a JOIN: it read each emitted argument's <c>[[buffer(n)]]</c>, took
    /// the SPIR-V id out of the argument's <c>_&lt;id&gt;</c> name, resolved that id to a declared element through
    /// the stage's own <c>DescriptorSet</c> and <c>Binding</c> decorations, and reported that the index it found
    /// agreed with the incumbent's arithmetic on all 159 arguments. Row 10 deleted that join from the binding
    /// path, because <c>MslIndexRemap</c> now STATES each index before the emission. Deleting the measurement
    /// with it would have left the engine asserting the emission carries its authored indices and never checking
    /// it, and the parse plus the decoration walk are exactly the instrument that can check. So they live here,
    /// as a test oracle, which is the one place 2.2b's ruling was always happy to have them.
    /// </para>
    /// <para>
    /// WHAT IT ASSERTS IS THE ROUND TRIP, and both directions of it. Every resource argument the emission
    /// carries resolves to a declared element, and the index it carries is the one the shipped table holds for
    /// that element in that stage. And every entry the table holds has an argument, so a table cannot quietly
    /// gain a bind the emission never asked for. A one-directional check passes on a table that binds nothing.
    /// </para>
    /// <para>
    /// THE COORDINATES ARE THE INTERESTING PART. The decorations give the RAW <c>(set, binding)</c> the author
    /// wrote, and every backend indexes the reflected layout POSITIONALLY, which is dense per set. This file
    /// folds the raw bindings into positions itself, out of the modules, rather than asking
    /// <c>SpirvCrossReflect</c>, so a fold that started dropping or reordering a resource is a red row here
    /// instead of a consistent pair of wrong answers.
    /// </para>
    /// <para>
    /// FOUR CONTROLS, because a check that reports 100 per cent is exactly where a vacuous measurement hides.
    /// The walk has to find a real corpus. The authored index has to differ from the element's position often
    /// enough to be measuring something a per-set count would not give. Stages really do omit elements, so the
    /// partial-stage case is exercised. And no two arguments in one stage may resolve to one element.
    /// </para>
    /// <para>
    /// Device-free, on every leg, on every <c>dotnet test</c>.
    /// </para>
    /// </summary>
    public sealed class MetalMslAuthoredIndexTests
    {
        readonly ITestOutputHelper _output;

        public MetalMslAuthoredIndexTests(ITestOutputHelper output) => _output = output;

        /// <summary>Every counter the walk produces, so the report and the assertions read the same numbers
        /// rather than two independently maintained sets.</summary>
        sealed class Census
        {
            internal int Programs, Arguments, Matched, TableEntries;
            internal int IndexDiffersFromPosition, StageElementSlots, UnreferencedSlots, Collisions;
            internal readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);
            internal readonly List<string> Detail = new();

            internal void Fail(string cls, string detail)
            {
                Failures.TryGetValue(cls, out int n);
                Failures[cls] = n + 1;
                if (Detail.Count < 30) Detail.Add($"{cls}: {detail}");
            }

            internal string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"programs={Programs} emittedArguments={Arguments} matchedTheAuthoredTable={Matched}");
                sb.AppendLine($"table entries={TableEntries} collisions={Collisions}");
                sb.AppendLine($"authored index differs from the element's position={IndexDiffersFromPosition}");
                sb.AppendLine($"stage/element slots={StageElementSlots} unreferenced by their stage={UnreferencedSlots}");
                foreach ((string cls, int n) in Failures.OrderByDescending(p => p.Value))
                    sb.AppendLine($"  FAILURE CLASS x{n}: {cls}");
                foreach (string line in Detail) sb.AppendLine("  " + line);
                return sb.ToString();
            }
        }

        [Fact]
        public void EveryShippedProgram_IsEmittedAtTheIndicesTheEngineAuthored()
        {
            var census = new Census();

            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                MetalMslProgram built = MetalShaderBuild.Pair(
                    program.VertexGlsl, program.FragmentGlsl, null, program.Name);

                Measure(census, program.Name, built, new[]
                {
                    (MetalShaderStage.Vertex, "vertex ",
                        SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name)),
                    (MetalShaderStage.Fragment, "fragment ",
                        SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name)),
                });
            }

            foreach (ShippedComputeKernel kernel in ShippedShaderPrograms.ComputeKernels())
            {
                MetalMslProgram built = MetalShaderBuild.Compute(kernel.ComputeGlsl, null, kernel.Name).Program;
                Measure(census, kernel.Name, built, new[]
                {
                    (MetalShaderStage.Compute, "kernel ",
                        SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name)),
                });
            }

            string report = census.Report();
            _output.WriteLine(report);

            // Not vacuous: an emptied catalog would otherwise satisfy an "everything matched" assertion by having
            // nothing to match, which is the failure mode the sibling spikes record in their own headers.
            Assert.True(census.Programs > 30 && census.Arguments > 100,
                "the shipped-program walk found almost nothing, so the assertions below mean nothing:\n" + report);

            // THE HEADLINE, forwards: every emitted resource argument carries the index the table holds for it.
            Assert.Empty(census.Failures);
            Assert.True(census.Matched == census.Arguments,
                "an emitted argument does not carry the index the engine authored for its element. The whole of "
                + "row 10 is that the engine states the index instead of reading it, so a mismatch here is a "
                + "resource bound where another was expected, on Metal, silently:\n" + report);

            // AND BACKWARDS: the table holds nothing the emission did not ask for. Without this the row passes on
            // a table that binds an element no function reads.
            Assert.True(census.TableEntries == census.Arguments,
                "the binding table and the emitted argument lists disagree on how many binds this corpus has:\n"
                + report);

            Assert.True(census.Collisions == 0,
                "two arguments in one stage resolved to the same layout element:\n" + report);

            // POSITIVE CONTROLS. The authored index is not trivially the element's position, and stages really do
            // omit elements, so neither half of the walk is a degenerate case.
            Assert.True(census.IndexDiffersFromPosition > 0,
                "every authored index now equals its element's position in the layout, which would make this "
                + "walk measure nothing a per-set count does not already give:\n" + report);
            Assert.True(census.UnreferencedSlots > 0,
                "every stage now references every element of its layout, so this walk no longer exercises the "
                + "partial-stage case at all:\n" + report);
        }

        static void Measure(Census census, string program, MetalMslProgram built,
            (MetalShaderStage Stage, string Keyword, byte[] Spirv)[] stages)
        {
            census.Programs++;
            IReadOnlyList<GpuResourceLayoutDescription> layouts = built.Table.Layouts;
            int totalElements = layouts.Sum(l => l.Elements.Length);
            Dictionary<(uint Set, uint Binding), int> positions = Positions(stages.Select(s => s.Spirv), program);

            census.TableEntries += built.Table.Count;

            foreach ((MetalShaderStage stage, string keyword, byte[] spirv) in stages)
            {
                IReadOnlyDictionary<uint, SpirvResourceDecoration> decorations =
                    SpirvResourceDecorations.Read(spirv, program + "." + stage);
                List<EmittedArgument> arguments = MslEntryPointArguments.Parse(built.StageOf(stage).Msl, keyword);

                census.StageElementSlots += totalElements;
                census.UnreferencedSlots += totalElements - arguments.Count;

                var seen = new HashSet<(int Set, int Position)>();
                foreach (EmittedArgument argument in arguments)
                {
                    census.Arguments++;
                    string where = $"{program}.{stage} [[{argument.Space}({argument.Index})]] '{argument.Name}'";

                    if (!MslEntryPointArguments.TryReadId(argument.Name, out uint id))
                    { census.Fail("argument name is not an _<id>", where); continue; }
                    if (!decorations.TryGetValue(id, out SpirvResourceDecoration decoration))
                    { census.Fail("id carries no set and binding in this stage's module", where); continue; }
                    if (!positions.TryGetValue((decoration.Set, decoration.Binding), out int position))
                    { census.Fail("decorated (set, binding) is in no module of this program", where); continue; }

                    if (!seen.Add(((int)decoration.Set, position))) census.Collisions++;
                    if (argument.Index != position) census.IndexDiffersFromPosition++;

                    if (!built.Table.TryGetIndex((int)decoration.Set, position, stage,
                            out MetalIndexTableEntry entry))
                    {
                        census.Fail("the table has no entry for an element the emission carries an argument for",
                            $"{where} set={decoration.Set} position={position}");
                        continue;
                    }

                    if (entry.Space.Word() != argument.Space || entry.Index != argument.Index)
                    {
                        census.Fail("the emitted index is not the authored one",
                            $"{where} set={decoration.Set} position={position} authored="
                            + $"[[{entry.Space.Word()}({entry.Index})]]");
                        continue;
                    }

                    census.Matched++;
                }
            }
        }

        /// <summary>
        /// The raw <c>(set, binding)</c> to layout POSITION fold, derived from the modules themselves rather than
        /// asked of <c>SpirvCrossReflect</c>. A set's elements are its bindings in ascending order, dense from 0,
        /// and the union across every stage of the program is what a layout describes.
        /// </summary>
        static Dictionary<(uint Set, uint Binding), int> Positions(IEnumerable<byte[]> modules, string program)
        {
            var all = new SortedSet<(uint Set, uint Binding)>();
            foreach (byte[] module in modules)
            {
                foreach (SpirvResourceDecoration decoration in
                         SpirvResourceDecorations.Read(module, program).Values)
                {
                    all.Add((decoration.Set, decoration.Binding));
                }
            }

            var positions = new Dictionary<(uint Set, uint Binding), int>(all.Count);
            uint set = uint.MaxValue;
            int position = 0;
            foreach ((uint s, uint binding) in all)
            {
                if (s != set) { set = s; position = 0; }
                positions[(s, binding)] = position++;
            }

            return positions;
        }
    }
}
