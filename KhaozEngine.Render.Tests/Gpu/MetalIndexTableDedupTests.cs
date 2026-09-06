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
    /// THE CONTENT DEDUPLICATION OF ROW 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/576), DEVICE-FREE.
    /// Decision M-R9 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para>
    /// WHAT THE ROW IS FOR. A pipeline switch invalidates a recorded slot only where the incoming program's index
    /// table maps that slot's elements to different indices than the outgoing one did, and Metal's argument tables
    /// are absolute and per encoder so the bound resources are otherwise still there. Every table built is a fresh
    /// object, so without deduplication that comparison is a reference test which is NEVER equal: every pipeline
    /// switch invalidates everything, which is exactly what <c>MTLCommandList.SetPipelineCore</c> already does by
    /// clearing its whole active-set array. This file is what says the comparison can be a handle compare.
    /// </para>
    /// <para>
    /// THE EQUIVALENCE IS THE LOAD-BEARING ASSERTION, not the hit rate. Two tables sharing an instance must be
    /// interchangeable through every member of the type, so the row that matters is the one walking every pair of
    /// shipped programs that dedup onto one instance and checking that their entries and their layout shapes
    /// really do agree. The hit rate is reported rather than asserted at a number, because it is a property of the
    /// shader catalog rather than of the mechanism.
    /// </para>
    /// <para>
    /// NO DEVICE ANYWHERE. <c>MetalShaderBuild</c> is the device-free half of the shader path, so the whole of
    /// this runs on the free Linux leg on every <c>dotnet test</c>, which is where a dedup that started merging
    /// tables it should not has to be caught: on a device it would present as a pipeline switch failing to rebind
    /// a resource, which is a wrong pixel with no error attached.
    /// </para>
    /// </summary>
    public sealed class MetalIndexTableDedupTests
    {
        readonly ITestOutputHelper _output;

        public MetalIndexTableDedupTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE WHOLE MECHANISM IN ONE ROW, and it needs no catalog: two independent builds of one program produce
        /// two distinct table objects, and the cache hands both callers the first one. That is the property row 13
        /// binds on, and the positive control in the middle is what makes it a measurement rather than a
        /// tautology.
        /// </summary>
        [Fact]
        public void TwoBuildsOfOneProgram_ShareOneTableThroughTheCache()
        {
            ShippedGraphicsProgram program = ShippedShaderPrograms.GraphicsPrograms().First();

            MetalShaderIndexTable first = MetalShaderBuild.Pair(
                program.VertexGlsl, program.FragmentGlsl, null, program.Name).Table;
            MetalShaderIndexTable second = MetalShaderBuild.Pair(
                program.VertexGlsl, program.FragmentGlsl, null, program.Name).Table;

            // WITHOUT THE CACHE THE COMPARISON IS NEVER EQUAL. This is the incumbent's behaviour stated as a
            // measurement rather than as a claim: same program, same emission, two objects.
            Assert.NotSame(first, second);
            Assert.False(first.SameIndicesAs(second));
            Assert.Equal(first.ContentKey, second.ContentKey);

            var cache = new MetalIndexTableCache();
            MetalShaderIndexTable canonicalFirst = cache.Canonical(first);
            MetalShaderIndexTable canonicalSecond = cache.Canonical(second);

            Assert.Same(first, canonicalFirst);
            Assert.Same(first, canonicalSecond);
            Assert.True(canonicalFirst.SameIndicesAs(canonicalSecond));
            Assert.Equal(1, cache.Count);
        }

        /// <summary>
        /// EVERY SHIPPED PROGRAM THROUGH ONE CACHE, and the assertion is the EQUIVALENCE rather than the hit rate:
        /// for every pair of programs the cache merged onto one instance, the two tables agree on every entry and
        /// on the layout shape <c>RequireLayoutShape</c> compares. A merge that did not would hand one pipeline
        /// another program's answers, which is the silent wrong bind this whole area exists to close.
        /// </summary>
        [Fact]
        public void EveryShippedProgram_DedupsOnlyOntoTablesItAgreesWith()
        {
            var cache = new MetalIndexTableCache();

            // Keyed by INSTANCE, which is what the default comparer gives here: the table overrides neither
            // Equals nor GetHashCode, deliberately, because content equality is ContentKey's job and having two
            // notions of table identity is the thing row 9 named the seat to prevent.
            var byInstance =
                new Dictionary<MetalShaderIndexTable, List<(string Name, MetalShaderIndexTable Table)>>();
            int programs = 0;

            foreach ((string name, MetalShaderIndexTable table) in ShippedTables())
            {
                programs++;
                MetalShaderIndexTable canonical = cache.Canonical(table);

                if (!byInstance.TryGetValue(canonical, out List<(string, MetalShaderIndexTable)>? sharing))
                    byInstance[canonical] = sharing = [];

                sharing.Add((name, table));
            }

            var report = new StringBuilder();
            report.AppendLine($"programs={programs} distinct tables={cache.Count}");

            int merged = 0, namesDiffered = 0;
            foreach ((MetalShaderIndexTable canonical, List<(string Name, MetalShaderIndexTable Table)> sharing)
                in byInstance)
            {
                if (sharing.Count == 1) continue;

                merged += sharing.Count - 1;
                report.AppendLine("  shared by " + string.Join(", ", sharing.Select(s => s.Name)));

                foreach ((string name, MetalShaderIndexTable table) in sharing)
                {
                    AssertInterchangeable(canonical, table, name);
                    if (!NamesAgree(canonical, table)) namesDiffered++;
                }
            }

            report.AppendLine($"programs merged onto an earlier table={merged}");
            report.AppendLine($"of those, element NAMES disagreed on={namesDiffered}");
            _output.WriteLine(report.ToString());

            // NOT VACUOUS. An emptied catalog would satisfy every assertion above by having nothing to merge.
            Assert.True(programs > 30, "the shipped-program walk found almost nothing:\n" + report);

            // POSITIVE CONTROL FOR THE OTHER DIRECTION: the cache is not collapsing the whole catalog onto one
            // table, which would satisfy the equivalence check only by being wrong about everything at once.
            Assert.True(cache.Count > 1, "every shipped program now deduplicates onto ONE table:\n" + report);
        }

        /// <summary>
        /// THE COLLISION ROW 9 CLOSED, ASSERTED THROUGH THE CACHE THIS ROW BUILT ON IT. Two tables with the same
        /// single entry and different layouts must come back as two instances: the key renders the layout shape,
        /// so they never meet. Sharing would hand pipeline B a table carrying program A's layouts, and
        /// <c>RequireLayoutShape</c> would then refuse B's own perfectly correct declared array.
        /// </summary>
        [Fact]
        public void TablesWithTheSameEntriesAndDifferentLayouts_DoNotShareAnInstance()
        {
            var cache = new MetalIndexTableCache();

            MetalShaderIndexTable withTexture = cache.Canonical(
                OneEntry(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly));
            MetalShaderIndexTable withSampler = cache.Canonical(
                OneEntry(GpuResourceKind.UniformBuffer, GpuResourceKind.Sampler));

            Assert.Equal(1, withTexture.Count);
            Assert.Equal(withTexture.Count, withSampler.Count);
            Assert.False(withTexture.SameIndicesAs(withSampler));
            Assert.Equal(2, cache.Count);

            // AND THE SHARED TABLE REALLY WOULD HAVE BEEN REFUSED. This is the consequence spelled out rather than
            // implied: the second program's own correct declared array against the first program's table.
            Assert.Throws<ShaderValidationException>(() => withTexture.RequireLayoutShape(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.Sampler), "pipeline B"));

            // POSITIVE CONTROL: the same content does merge, so the row above is not passing because the cache
            // never merges anything.
            Assert.Same(withTexture,
                cache.Canonical(OneEntry(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly)));
            Assert.Equal(2, cache.Count);
        }

        /// <summary>
        /// THE ELEMENT NAME IS NOT OBSERVABLE THROUGH A TABLE, and that is what makes merging two programs that
        /// disagree on names sound rather than merely lucky. <c>ContentKey</c> renders the layout SHAPE and the
        /// entries and deliberately not the names, because nothing the table answers reads one: the shape check
        /// compares kinds and the join reaches an element only for its kind.
        /// <para>
        /// AND THE COST OF GETTING THAT WRONG LATER IS MEASURED RATHER THAN IMAGINED. Over the shipped catalog 25
        /// programs merge onto an earlier table and 16 of those disagree on at least one element name
        /// (<see cref="EveryShippedProgram_DedupsOnlyOntoTablesItAgreesWith"/> reports both numbers), so a member
        /// that started reading a name off <c>Layouts</c> would be reading another program's name in the majority
        /// of merges rather than in a corner case.
        /// </para>
        /// <para>
        /// WHAT THIS ROW DOES NOT CATCH is a NEW member that reads a name: it pins the two mechanisms that exist
        /// (the key and the shape check) and nothing about members that do not exist yet. Making the constraint
        /// mechanical, as an IL walk over the table's own methods, is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/594.
        /// </para>
        /// </summary>
        [Fact]
        public void TwoTablesDifferingOnlyInElementNames_AreInterchangeable()
        {
            MetalShaderIndexTable mine = OneEntryNamed("Frame", GpuResourceKind.UniformBuffer);
            MetalShaderIndexTable theirs = OneEntryNamed("Params", GpuResourceKind.UniformBuffer);

            Assert.Equal(mine.ContentKey, theirs.ContentKey);
            AssertInterchangeable(mine, theirs, "named differently");

            var cache = new MetalIndexTableCache();
            Assert.Same(cache.Canonical(mine), cache.Canonical(theirs));
            Assert.Equal(1, cache.Count);

            // The names really did differ, so this row is not passing because both layouts say the same thing.
            Assert.NotEqual(mine.Layouts[0].Elements[0].Name, theirs.Layouts[0].Elements[0].Name);
        }

        /// <summary>
        /// A TABLE THAT NEVER WENT THROUGH THE CACHE COMPARES UNEQUAL TO ITS OWN TWIN, which is the safe
        /// direction: row 13 invalidates a slot it did not have to rather than keeping a stale index. Pinned
        /// because it is the exact cost of adding a second route to a table, and the next reader deserves to know
        /// which way the mistake falls.
        /// </summary>
        [Fact]
        public void ATableBuiltOutsideTheCache_ComparesUnequalToItsTwin()
        {
            MetalShaderIndexTable outside = OneEntry(GpuResourceKind.UniformBuffer);
            MetalShaderIndexTable inside = new MetalIndexTableCache().Canonical(
                OneEntry(GpuResourceKind.UniformBuffer));

            Assert.Equal(outside.ContentKey, inside.ContentKey);
            Assert.False(outside.SameIndicesAs(inside));
            Assert.True(outside.SameIndicesAs(outside));
            Assert.False(outside.SameIndicesAs(null));
        }

        // Everything two tables sharing one instance would have to agree on, checked from OUTSIDE the key that
        // merged them: the entries row 13 binds through, and the shape pin 4 compares a declared array against.
        static void AssertInterchangeable(MetalShaderIndexTable canonical, MetalShaderIndexTable table, string name)
        {
            Assert.Equal(canonical.Count, table.Count);

            List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>> mine = canonical.Entries().ToList();
            List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>> theirs = table.Entries().ToList();
            Assert.Equal(mine.Count, theirs.Count);

            for (int i = 0; i < mine.Count; i++)
            {
                Assert.Equal(mine[i].Key, theirs[i].Key);
                Assert.Equal(mine[i].Value, theirs[i].Value);
            }

            // The shape check, driven BOTH WAYS, because that is what a shared table has to survive: the merged
            // program's declared array against the canonical table, which is the call row 11 makes at pipeline
            // creation and the one a lossy key would break.
            canonical.RequireLayoutShape(table.Layouts, name);
            table.RequireLayoutShape(canonical.Layouts, name);
        }

        // Whether the two tables' layouts agree on element NAMES, which the key deliberately does not render
        // because nothing on the table reads one. Reported rather than asserted: a non-zero count is the standing
        // evidence that names really are unobservable through this object, and the day something starts reading
        // one off Layouts, this number is the size of the problem that creates.
        static bool NamesAgree(MetalShaderIndexTable canonical, MetalShaderIndexTable table)
        {
            for (int set = 0; set < canonical.Layouts.Count; set++)
            {
                GpuResourceLayoutElement[] mine = canonical.Layouts[set].Elements;
                GpuResourceLayoutElement[] theirs = table.Layouts[set].Elements;
                for (int i = 0; i < mine.Length; i++)
                {
                    if (!string.Equals(mine[i].Name, theirs[i].Name, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }

        static IEnumerable<(string Name, MetalShaderIndexTable Table)> ShippedTables()
        {
            foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            {
                yield return (program.Name,
                    MetalShaderBuild.Pair(program.VertexGlsl, program.FragmentGlsl, null, program.Name).Table);
            }

            foreach (ShippedComputeKernel kernel in ShippedShaderPrograms.ComputeKernels())
                yield return (kernel.Name, MetalShaderBuild.Compute(kernel.ComputeGlsl, null, kernel.Name).Program.Table);
        }

        // A table over the given layout whose fragment stage references element 0 and nothing else, from the
        // refusal suite's own helper shape. The first kind has to be a buffer kind, because the one argument is a
        // [[buffer(0)]].
        /// <summary>
        /// A DECLARED LAYOUT THAT BINDS NOTHING IS THE SAME SHAPE AS NO LAYOUT (#599, landed with the toolchain
        /// swap). A resource-free shader reflects zero sets since 18.0.0, and a pipeline created against it with
        /// <c>ResourceLayouts = []</c> or with one empty declared layout is legal either way: trailing empty
        /// declared layouts are trimmed before the count is compared. An empty layout in the MIDDLE is not
        /// trimmed, because sets are positional, and a declared element the shader never reflected is still
        /// refused.
        /// </summary>
        [Fact]
        public void TrailingEmptyDeclaredLayouts_AreTheSameShapeAsNone()
        {
            MetalShaderIndexTable resourceFree = MetalShaderIndexTable.Build(
                [], Array.Empty<MetalStageResourceUse>(), "resource-free");
            MetalShaderIndexTable oneBuffer = OneEntry(GpuResourceKind.UniformBuffer);

            resourceFree.RequireLayoutShape([], "nothing declared");
            resourceFree.RequireLayoutShape([new GpuResourceLayoutDescription()], "one empty declared");
            resourceFree.RequireLayoutShape(
                [new GpuResourceLayoutDescription(), new GpuResourceLayoutDescription()], "two empty declared");
            Assert.Throws<ShaderValidationException>(() => resourceFree.RequireLayoutShape(
                Layout(GpuResourceKind.UniformBuffer), "an element the shader never reflected"));

            oneBuffer.RequireLayoutShape(
                [Layout(GpuResourceKind.UniformBuffer)[0], new GpuResourceLayoutDescription()], "trailing empty");
            Assert.Throws<ShaderValidationException>(() => oneBuffer.RequireLayoutShape(
                [new GpuResourceLayoutDescription(), Layout(GpuResourceKind.UniformBuffer)[0]], "leading empty"));
        }

        static MetalShaderIndexTable OneEntry(params GpuResourceKind[] kinds) => OneEntry(Layout("e", kinds));

        // The same table with the elements named something else, for the name-invariance row.
        static MetalShaderIndexTable OneEntryNamed(string prefix, params GpuResourceKind[] kinds)
            => OneEntry(Layout(prefix, kinds));

        static MetalShaderIndexTable OneEntry(GpuResourceLayoutDescription[] layouts)
            => MetalShaderIndexTable.Build(
                layouts,
                new[]
                {
                    new MetalStageResourceUse(MetalShaderStage.Fragment, new[] { new MslResourceRef(0, 0) }),
                },
                "hand-built");

        static GpuResourceLayoutDescription[] Layout(params GpuResourceKind[] kinds) => Layout("e", kinds);

        static GpuResourceLayoutDescription[] Layout(string prefix, params GpuResourceKind[] kinds)
        {
            var elements = new GpuResourceLayoutElement[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
                elements[i] = new GpuResourceLayoutElement(prefix + i, kinds[i], GpuShaderStages.Fragment);
            return [new GpuResourceLayoutDescription(elements)];
        }
    }
}
