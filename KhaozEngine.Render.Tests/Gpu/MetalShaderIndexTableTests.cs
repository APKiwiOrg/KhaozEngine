using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-T3, AS AN ASSERTION RATHER THAN A DETECTOR, over every shipped program, device-free, on every
    /// <c>dotnet test</c>, taken before the first golden run. Section 2.2b of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> is the ruling this guards.
    ///
    /// <para>
    /// WHAT SEPARATES THIS FROM <see cref="MetalMslAuthoredIndexTests"/>, which asserts the same properties. That
    /// one RE-IMPLEMENTS the join inside the test, because it was measuring whether a mechanism could exist. This
    /// one drives the SHIPPED mechanism, <c>MetalShaderBuild</c> and <c>MetalShaderIndexTable</c>, over the same
    /// corpus. A test that re-implements what it checks passes forever while the shipped path rots beside it,
    /// which is the failure phase 2's byte-equality header records in its own words. Both are kept: the spike is
    /// the ruling's evidence and this is the ruling's guard.
    /// </para>
    /// <para>
    /// THE FAILURE THIS CATCHES IS "EVERYTHING COMPILES AND EVERY PIXEL IS WRONG", which is what
    /// <c>ShaderValidation.CheckMslBufferSlots</c> and the Vulkan binding-table test both exist for, arriving
    /// through the one door Metal leaves open. Three recorded production incidents (7.25.0's model pass reading
    /// the normal texture through the albedo sampler, 7.51.2's crease term reading depth data, and the splat
    /// terrain reading the frame UBO's bytes through the params UBO) are the shape, and every one of them was a
    /// silent wrong render rather than an error.
    /// </para>
    /// <para>
    /// FOUR OF THE FIVE ASSERTIONS ARE ENFORCED BY THE MECHANISM ITSELF, so this exercises them by running it: a
    /// name that is not <c>_&lt;id&gt;</c>, an id with no decorations in that stage's module, a
    /// <c>(set, binding)</c> outside the declared array, a kind that does not match its index space, and two
    /// arguments resolving to one element all THROW inside <c>MetalShaderIndexTable.Build</c>. The refusals are
    /// driven directly in <see cref="MetalShaderIndexTableRefusalTests"/>. What this file adds on top is the
    /// census, so an emptied catalog cannot satisfy the assertions by having nothing to check, and the sixth
    /// assertion below, which no mechanism enforces.
    /// </para>
    /// <para>
    /// THE SIXTH ASSERTION IS THE INCUMBENT COMPARISON, and 2.2b keeps it through the rollout window on purpose.
    /// Over the shipped set the table and <c>MTLResourceLayout</c>'s per-kind declaration-order arithmetic agree
    /// on every argument, so "this backend changes no binding" is a checked fact rather than a claim, and the
    /// golden family sees exactly one variable move on the day the backend lands. It is the one assertion a
    /// legitimate future divergence retires, and retiring it is a deliberate act with a recorded reason rather
    /// than a number to update.
    /// </para>
    /// </summary>
    public sealed class MetalShaderIndexTableTests
    {
        readonly ITestOutputHelper _output;

        public MetalShaderIndexTableTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void EveryShippedProgram_ResolvesEveryEmittedArgumentThroughItsDecorations()
        {
            int programs = 0, entries = 0, differsFromBinding = 0, differsFromIncumbent = 0;
            int stagesSeen = 0, unreferencedSlots = 0, stageElementSlots = 0;
            var report = new StringBuilder();
            var disagreements = new List<string>();
            var stopwatch = Stopwatch.StartNew();

            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                // Building at all is four of the five assertions: every refusal class is a throw in here.
                MetalMslProgram built = MetalShaderBuild.Pair(
                    program.VertexGlsl, program.FragmentGlsl, null, program.Name);
                Measure(program.Name, built, ref entries, ref differsFromBinding, ref differsFromIncumbent,
                    ref stagesSeen, ref stageElementSlots, ref unreferencedSlots, disagreements);
                programs++;
            }

            foreach (ShippedComputeKernel kernel in ShippedShaderPrograms.ComputeKernels())
            {
                (MetalMslProgram built, uint x, uint y, uint z) = MetalShaderBuild.Compute(
                    kernel.ComputeGlsl, null, kernel.Name);

                // The workgroup size rides the same call and is what dispatchThreadgroups needs, so a kernel that
                // built but reported nothing would be a silent zero-thread dispatch later.
                Assert.True(x >= 1 && y >= 1 && z >= 1,
                    $"{kernel.Name}: workgroup size {x}x{y}x{z} has a zero dimension.");

                Measure(kernel.Name, built, ref entries, ref differsFromBinding, ref differsFromIncumbent,
                    ref stagesSeen, ref stageElementSlots, ref unreferencedSlots, disagreements);
                programs++;
            }

            stopwatch.Stop();
            report.AppendLine($"programs={programs} stages={stagesSeen} tableEntries={entries}");
            report.AppendLine($"metal index differs from the binding number={differsFromBinding}");
            report.AppendLine($"stage/element slots={stageElementSlots} unreferenced by their stage={unreferencedSlots}");
            report.AppendLine($"metal index differs from the incumbent's arithmetic={differsFromIncumbent}");
            report.AppendLine($"whole-corpus emission and join: {stopwatch.ElapsedMilliseconds} ms");
            foreach (string line in disagreements.Take(20)) report.AppendLine("  " + line);
            _output.WriteLine(report.ToString());

            // NOT VACUOUS. An emptied catalog would otherwise satisfy every assertion above by having nothing to
            // resolve, which is the failure mode the sibling spikes record in their own headers.
            Assert.True(programs > 30 && entries > 100,
                "the shipped-program walk found almost nothing, so the assertions above mean nothing:\n" + report);

            // POSITIVE CONTROLS, so neither half of the census is the degenerate case. The table is not trivially
            // the binding number, and stages really do omit elements.
            Assert.True(differsFromBinding > 0,
                "every emitted metal index now equals its binding number, which would make this table measure "
                + "nothing a per-set count does not already give:\n" + report);
            Assert.True(unreferencedSlots > 0,
                "every stage now references every element of its layout, so this no longer exercises the "
                + "partial-stage case that makes 'not bound for a stage with no entry' load-bearing:\n" + report);

            // THE SIXTH ASSERTION (2.2b). Kept through the rollout window as the evidence that this backend
            // changes no binding.
            Assert.True(differsFromIncumbent == 0,
                "a shipped program's emitted index now disagrees with the incumbent's per-kind declaration-order "
                + "arithmetic. That is the id join being VINDICATED rather than a regression: the incumbent would "
                + "bind the wrong resource for that program and render a wrong pixel with no validation error, "
                + "and this table binds where the emission actually put it. Read section 2.2b before touching "
                + "this assertion. Retiring it is a deliberate act with a recorded reason, not a number to "
                + "update:\n" + report);
        }

        /// <summary>
        /// The entry-point NAME is read out of the emission rather than assumed to be <c>main0</c> (M-S5), and
        /// this is what says so about the real corpus. The incumbent looks a function up by a name Veldrid
        /// supplies from a layer this backend does not have, so a hardcoded guess here would be inheriting a
        /// convention through a gap.
        /// </summary>
        [Fact]
        public void EveryShippedProgram_CarriesAnEntryPointNameItRead()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                MetalMslProgram built = MetalShaderBuild.Pair(
                    program.VertexGlsl, program.FragmentGlsl, null, program.Name);

                Assert.Equal(2, built.Stages.Count);
                foreach (MetalMslStage stage in built.Stages)
                {
                    Assert.False(string.IsNullOrWhiteSpace(stage.EntryPointName));
                    Assert.False(string.IsNullOrWhiteSpace(stage.Msl));
                    names.Add(stage.EntryPointName);
                }

                Assert.Equal(MetalShaderStage.Vertex, built.StageOf(MetalShaderStage.Vertex).Stage);
                Assert.Equal(MetalShaderStage.Fragment, built.StageOf(MetalShaderStage.Fragment).Stage);
            }

            _output.WriteLine("entry-point names across the shipped graphics set: " + string.Join(", ", names));

            // TODAY THAT SET IS {main0}, AND THIS DOES NOT ASSERT IT. The value of reading the name is that the
            // day SPIRV-Cross emits something else, the backend follows and nothing has to be edited. Pinning the
            // literal here would turn that into a red test for a change that is correctly absorbed.
            Assert.NotEmpty(names);
        }

        void Measure(string program, MetalMslProgram built, ref int entries, ref int differsFromBinding,
            ref int differsFromIncumbent, ref int stagesSeen, ref int stageElementSlots, ref int unreferencedSlots,
            List<string> disagreements)
        {
            IReadOnlyList<GpuResourceLayoutDescription> layouts = built.Table.Layouts;
            int totalElements = layouts.Sum(l => l.Elements.Length);
            var incumbent = IncumbentIndices(layouts);
            var perStage = new Dictionary<MetalShaderStage, int>();

            foreach ((MetalIndexTableKey key, MetalIndexTableEntry entry) in built.Table.Entries())
            {
                entries++;
                perStage.TryGetValue(key.Stage, out int n);
                perStage[key.Stage] = n + 1;

                // The positional assumption, checked from OUTSIDE the mechanism that enforces it, so this test
                // fails rather than the mechanism silently widening one day.
                Assert.InRange(key.Set, 0, layouts.Count - 1);
                Assert.InRange(key.Binding, 0, layouts[key.Set].Elements.Length - 1);

                GpuResourceKind kind = layouts[key.Set].Elements[key.Binding].Kind;
                Assert.True(entry.Space.MatchesKind(kind),
                    $"{program}: set {key.Set} binding {key.Binding} is a {kind} and landed in the "
                    + $"{entry.Space.Word()} space.");

                if (entry.Index != key.Binding) differsFromBinding++;

                (int buffer, int texture, int sampler) = incumbent[(key.Set, key.Binding)];
                int incumbentIndex = entry.Space switch
                {
                    MetalIndexSpace.Buffer => buffer,
                    MetalIndexSpace.Texture => texture,
                    _ => sampler,
                };
                if (entry.Index != incumbentIndex)
                {
                    differsFromIncumbent++;
                    disagreements.Add($"DISAGREES {program}.{key.Stage} [[{entry.Space.Word()}({entry.Index})]] "
                        + $"set={key.Set} binding={key.Binding} incumbentWouldSay={incumbentIndex}");
                }
            }

            foreach (MetalMslStage stage in built.Stages)
            {
                stagesSeen++;
                stageElementSlots += totalElements;
                perStage.TryGetValue(stage.Stage, out int referenced);
                unreferencedSlots += totalElements - referenced;

                // STRUCTURAL: every resource argument the emission carries reached the table, per stage. The
                // census floor below counts the CORPUS rather than one program, so without this a single missing
                // entry would sit invisible inside a total of more than a hundred and read as an element the
                // stage never referenced. Counting the attributes is enough here because
                // MetalMslAuthoredIndexTests checks the far stronger property, which is that each of those
                // attributes names the index the engine authored for the element it belongs to.
                Assert.Equal(ResourceAttributes(stage.Msl), referenced);
            }
        }

        // How many [[buffer(n)]], [[texture(n)]] and [[sampler(n)]] attributes one stage's emitted MSL carries.
        // Every resource argument has exactly one and no other argument has any: stage_in, the return value's
        // position and every builtin carry a different attribute or none.
        static int ResourceAttributes(string msl)
        {
            int count = 0;
            foreach (string marker in new[] { "[[buffer(", "[[texture(", "[[sampler(" })
            {
                for (int at = msl.IndexOf(marker, StringComparison.Ordinal); at >= 0;
                     at = msl.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// <c>MTLResourceLayout</c>'s and <c>GetBufferBase</c>'s arithmetic, reproduced here as the STANDING
        /// GUARD, over the engine's own mirror types. It is reproduced a second time in
        /// <see cref="MetalMslAuthoredIndexTests"/>, over Veldrid's types, as the ruling's EVIDENCE, and both
        /// copies are deliberate: the spike measures the incumbent as the incumbent actually spells it, and this
        /// one keeps that comparison running against the shipped mechanism. What 2.2b requires is the part both
        /// of them keep, which is that the per-kind arithmetic is written as the COMPARISON and never as the
        /// binding path. Per-kind counters, with uniform and both structured kinds sharing a buffer counter and
        /// both texture kinds sharing a texture counter, accumulated across the preceding layouts in declaration
        /// order.
        ///
        /// <para>TWO COPIES IS THE LIMIT, WHICH IS WHY THIS ONE IS <c>internal</c>. MM6's mechanism row,
        /// <c>MetalTwoUniformBufferGpuTests.TheSplitStageProgramNowAgreesWithTheCount_BecauseTheIndexIsAuthored</c>, needs
        /// the same walk to say where the incumbent WOULD have put the buffer, and it reads this one rather than
        /// writing a third. So a correction to the arithmetic lands in both places at once, and the only other
        /// copy left is the spike's, which is deliberately over Veldrid's own types.</para>
        /// </summary>
        internal static Dictionary<(int Set, int Binding), (int Buffer, int Texture, int Sampler)> IncumbentIndices(
            IReadOnlyList<GpuResourceLayoutDescription> layouts)
        {
            var indices = new Dictionary<(int, int), (int, int, int)>();
            int buffers = 0, textures = 0, samplers = 0;

            for (int set = 0; set < layouts.Count; set++)
            {
                GpuResourceLayoutElement[] elements = layouts[set].Elements;
                for (int e = 0; e < elements.Length; e++)
                {
                    indices[(set, e)] = (buffers, textures, samplers);
                    switch (MetalIndexSpaces.For(elements[e].Kind))
                    {
                        case MetalIndexSpace.Buffer: buffers++; break;
                        case MetalIndexSpace.Texture: textures++; break;
                        default: samplers++; break;
                    }
                }
            }

            return indices;
        }
    }
}
