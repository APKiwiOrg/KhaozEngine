using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// PIN 1 OF SECTION 2.2b, DRIVEN, AS 18.0.0 LEFT IT: <b>the binding table fails LOUDLY and never falls back.</b>
    /// Every way it can fail throws at shader-set creation, device-free, naming the program and the stage.
    ///
    /// <para>
    /// SEVEN REFUSALS BECAME SIX, AND THE THREE THAT WENT ARE THE THREE THAT CANNOT HAPPEN ANY MORE. Row 10
    /// (#693) authored the indices instead of parsing them out of the emitted MSL, so an argument name that is
    /// not <c>_&lt;id&gt;</c>, a SPIR-V id carrying no decorations in that stage's module, and a malformed index
    /// attribute are all failures to RESOLVE an argument, and nothing resolves an argument any more. The kind
    /// check went too, for a better reason than disuse: the index space is DERIVED from the element's kind now,
    /// so a resource in the wrong space is unconstructible rather than merely rejected.
    /// </para>
    /// <para>
    /// WHAT IS LEFT IS STRUCTURAL, AND IT IS STILL THE MOST IMPORTANT PROPERTY IN THE ROW. A use list naming a
    /// set or a binding the layouts do not declare, or naming one element twice in one stage, is a payload or a
    /// caller that does not match the reflection, and a table built from it binds a resource where another was
    /// expected. 2.2b's ruling is unchanged: throw rather than fall back to a count, because a silent recovery
    /// reintroduces the incumbent's failure mode inside the mechanism that replaced it, and it is exactly the
    /// kind of helpful edit a later reader adds in good faith.
    /// </para>
    /// <para>
    /// AND THE ENTRY-POINT PARSE STILL HAS FOUR OF ITS OWN, because the NAME is still read out of the emission
    /// (M-S5). It is the only thing that is.
    /// </para>
    /// <para>
    /// HAND-BUILT INPUTS RATHER THAN SHIPPED SHADERS, deliberately. Every shipped program builds cleanly (that is
    /// <see cref="MetalShaderIndexTableTests"/>'s subject), so a refusal can only be reached by constructing the
    /// shape, and constructing it is also the clearest statement of what each refusal MEANS.
    /// </para>
    /// </summary>
    public sealed class MetalShaderIndexTableRefusalTests
    {
        [Fact]
        public void ASetPastTheDeclaredLayoutArray_IsRefused()
        {
            Assert.Contains("past the 1 layouts", Refusal(() => Build(
                Layout(GpuResourceKind.UniformBuffer), Used((Set: 1, Binding: 0)))),
                StringComparison.Ordinal);
        }

        [Fact]
        public void ABindingPastThatSetsElementArray_IsRefused()
        {
            Assert.Contains("declares 1 elements", Refusal(() => Build(
                Layout(GpuResourceKind.UniformBuffer), Used((Set: 0, Binding: 1)))),
                StringComparison.Ordinal);
        }

        /// <summary>One element is one argument in one stage. A use list naming it twice did not come from an
        /// emission, and the second would silently replace the first.</summary>
        [Fact]
        public void OneElementNamedTwiceInOneStage_IsRefused()
        {
            Assert.Contains("names it twice", Refusal(() => Build(
                Layout(GpuResourceKind.UniformBuffer), Used((Set: 0, Binding: 0), (Set: 0, Binding: 0)))),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// THE POSITIVE CONTROL FOR ALL THREE, and the row that states what "authored" means. The index a table
        /// records is the one <c>MslIndexRemap</c>'s scheme assigns, which is a per-space counter walked in
        /// ascending <c>(set, binding)</c> and is NOT the binding number: the layout below puts a second uniform
        /// buffer at binding 2, behind a texture at binding 1, so it lands at <c>buffer(1)</c> while the texture
        /// lands at <c>texture(0)</c>. Three different numbers for one element, and the table carries the one the
        /// emission was told to use.
        /// </summary>
        [Fact]
        public void ACleanBuild_RecordsTheAuthoredIndexRatherThanTheBindingNumber()
        {
            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly,
                    GpuResourceKind.UniformBuffer),
                Used((Set: 0, Binding: 0), (Set: 0, Binding: 1), (Set: 0, Binding: 2)),
                "hand-built");

            Assert.True(table.TryGetIndex(0, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry first));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 0), first);

            Assert.True(table.TryGetIndex(0, 1, MetalShaderStage.Fragment, out MetalIndexTableEntry texture));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Texture, 0), texture);

            Assert.True(table.TryGetIndex(0, 2, MetalShaderStage.Fragment, out MetalIndexTableEntry second));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 1), second);

            // AND A STAGE WITH NO ENTRY IS NOT BOUND FOR THAT STAGE, which is correct by construction rather than
            // a gap: SPIRV-Cross omits an argument a stage does not reference, and the engine asks it which.
            Assert.False(table.TryGetIndex(0, 0, MetalShaderStage.Vertex, out _));
        }

        /// <summary>
        /// TWO UNIFORM BUFFERS IN ONE LAYOUT GET TWO DISTINCT BUFFER INDICES, BY CONSTRUCTION. That is the shape
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/604 exists for: the incumbent's per-kind declaration
        /// count is what made a second uniform buffer per pipeline unsafe, and an authored scheme states the two
        /// indices instead of counting to them. This row does NOT lift #604's shipped validation, which is that
        /// issue's own change, and it is the device-free evidence that the numbering half of it is already done.
        /// </summary>
        [Fact]
        public void TwoUniformBuffersInOneLayout_GetTwoDistinctAuthoredBufferIndices()
        {
            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.UniformBuffer),
                Used((Set: 0, Binding: 0), (Set: 0, Binding: 1)),
                "two-ubo");

            Assert.True(table.TryGetIndex(0, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry first));
            Assert.True(table.TryGetIndex(0, 1, MetalShaderStage.Fragment, out MetalIndexTableEntry second));

            Assert.Equal(MetalIndexSpace.Buffer, first.Space);
            Assert.Equal(MetalIndexSpace.Buffer, second.Space);
            Assert.NotEqual(first.Index, second.Index);
            Assert.Equal(0, first.Index);
            Assert.Equal(1, second.Index);
        }

        /// <summary>
        /// AND THE SAME HOLDS ACROSS TWO SETS, which is the splat terrain's own shape: set 0 read by the vertex
        /// alone and set 1 by the fragment alone. The two uniform buffers still get distinct indices, and each
        /// stage's table carries only what that stage reads.
        /// </summary>
        [Fact]
        public void TwoUniformBuffersAcrossTwoSetsReadByDifferentStages_KeepDistinctIndices()
        {
            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                new[]
                {
                    new GpuResourceLayoutDescription(Elements(GpuResourceKind.UniformBuffer)),
                    new GpuResourceLayoutDescription(Elements(GpuResourceKind.UniformBuffer)),
                },
                new[]
                {
                    new MetalStageResourceUse(MetalShaderStage.Vertex, new[] { new MslResourceRef(0, 0) }),
                    new MetalStageResourceUse(MetalShaderStage.Fragment, new[] { new MslResourceRef(1, 0) }),
                },
                "split-sets");

            Assert.True(table.TryGetIndex(0, 0, MetalShaderStage.Vertex, out MetalIndexTableEntry vertex));
            Assert.True(table.TryGetIndex(1, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry fragment));

            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 0), vertex);
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 1), fragment);
            Assert.False(table.TryGetIndex(1, 0, MetalShaderStage.Vertex, out _));
            Assert.False(table.TryGetIndex(0, 0, MetalShaderStage.Fragment, out _));
        }

        /// <summary>THE FOUR THE ENTRY-POINT PARSE STILL OWNS. The name is the one thing still read out of the
        /// emission (M-S5), so an emission it cannot read is a stop rather than a guessed <c>main0</c>.</summary>
        [Fact]
        public void AnEntryPointTheParseCannotRead_IsRefusedRatherThanGuessed()
        {
            Assert.Contains("declares no 'fragment' entry point",
                NameRefusal("kernel main0()\n{\n}\n"), StringComparison.Ordinal);
            Assert.Contains("no argument list at all",
                NameRefusal("fragment main0_out main0\n"), StringComparison.Ordinal);
            Assert.Contains("never closes",
                NameRefusal("fragment main0_out main0(constant A& _7 [[buffer(0)]]\n"), StringComparison.Ordinal);
            Assert.Contains("name could not be read",
                NameRefusal("fragment ()\n{\n}\n"), StringComparison.Ordinal);
        }

        /// <summary>The other half of those four: a well-formed entry point of the same shape reads its name, so
        /// none of the rows above is passing against a parse that throws unconditionally.</summary>
        [Fact]
        public void AWellFormedEntryPoint_ReadsItsName()
        {
            Assert.Equal("main0", MetalMslEntryPoint.NameOf(
                EntryPoint("constant _68& _70 [[buffer(2)]]", "texture2d<float> _77 [[texture(0)]]"),
                MetalShaderStage.Fragment, "hand-built"));
        }

        /// <summary>
        /// PIN 4, DRIVEN: the declared layout array is shape-checked against the reflection the table was built
        /// from, at pipeline creation. Row 11 is the only caller, and this is what says the check is real.
        /// </summary>
        [Fact]
        public void ADeclaredLayoutArrayOfADifferentShape_IsRefusedByTheShapeCheck()
        {
            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly),
                Used((Set: 0, Binding: 0)),
                "hand-built");

            // The same shape passes, so the rows below are not passing because the check refuses everything.
            table.RequireLayoutShape(Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly), "ok");

            Assert.Contains("resource layouts", Refusal(() => table.RequireLayoutShape(
                new[]
                {
                    new GpuResourceLayoutDescription(Elements(GpuResourceKind.UniformBuffer)),
                    new GpuResourceLayoutDescription(Elements(GpuResourceKind.Sampler)),
                }, "two sets")), StringComparison.Ordinal);

            Assert.Contains("elements", Refusal(() => table.RequireLayoutShape(
                Layout(GpuResourceKind.UniformBuffer), "one element")), StringComparison.Ordinal);

            Assert.Contains("Sampler", Refusal(() => table.RequireLayoutShape(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.Sampler), "wrong kind")),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// ROW 10'S SEAT, PINNED: <c>ContentKey</c> is a COMPLETE identity of the table, layouts included. Two
        /// programs with the same entries and different layouts must not dedup onto one instance, because pin 4
        /// compares a pipeline's declared array against the table's layouts, so a shared table would answer
        /// pipeline B's own correct array with program A's reflection and refuse it.
        /// </summary>
        [Fact]
        public void TwoTablesWithTheSameEntriesAndDifferentLayouts_DoNotShareAContentKey()
        {
            // The two layouts disagree only at element 1, which NEITHER stage references, so both tables carry
            // the same single entry. That is the ordinary shape rather than a contrived one: over the shipped set
            // 95 of 254 stage/element slots are unreferenced by their stage.
            MetalShaderIndexTable withTexture = OneEntry(
                GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly);
            MetalShaderIndexTable withSampler = OneEntry(
                GpuResourceKind.UniformBuffer, GpuResourceKind.Sampler);

            Assert.Equal(1, withTexture.Count);
            Assert.Equal(withTexture.Count, withSampler.Count);
            Assert.True(withTexture.TryGetIndex(0, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry mine));
            Assert.True(withSampler.TryGetIndex(0, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry theirs));
            Assert.Equal(mine, theirs);

            Assert.NotEqual(withTexture.ContentKey, withSampler.ContentKey);

            // POSITIVE CONTROL, so the row above is not passing because every table gets its own key: the same
            // content really does render the same string, which is the whole point of the seam.
            Assert.Equal(
                withTexture.ContentKey,
                OneEntry(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly).ContentKey);
        }

        /// <summary>A table over the given layout whose fragment stage references element 0 and nothing else. The
        /// first kind has to be a buffer kind, because the assertions above read a buffer index.</summary>
        static MetalShaderIndexTable OneEntry(params GpuResourceKind[] kinds)
            => MetalShaderIndexTable.Build(Layout(kinds), Used((Set: 0, Binding: 0)), "hand-built");

        static MetalShaderIndexTable Build(GpuResourceLayoutDescription[] layouts,
            MetalStageResourceUse[] used)
            => MetalShaderIndexTable.Build(layouts, used, "hand-built");

        /// <summary>One fragment stage using the given elements, which is the shape every refusal above is built
        /// on: the stage half of the key is never what is under test here.</summary>
        static MetalStageResourceUse[] Used(params (int Set, int Binding)[] elements)
        {
            var refs = new MslResourceRef[elements.Length];
            for (int i = 0; i < elements.Length; i++)
                refs[i] = new MslResourceRef(elements[i].Set, elements[i].Binding);
            return new[] { new MetalStageResourceUse(MetalShaderStage.Fragment, refs) };
        }

        static string Refusal(Action call) => Assert.Throws<ShaderValidationException>(call).Message;

        static string NameRefusal(string msl)
            => Assert.Throws<ShaderValidationException>(
                () => MetalMslEntryPoint.NameOf(msl, MetalShaderStage.Fragment, "hand-built")).Message;

        /// <summary>A fragment entry point of the shape SPIRV-Cross emits, carrying the given argument text
        /// verbatim. The return type between the keyword and the name is not decoration: it is why the name is
        /// read backwards from the parenthesis rather than forwards from the keyword.</summary>
        static string EntryPoint(params string[] arguments)
            => "fragment main0_out main0(" + string.Join(", ", arguments) + ")\n{\n    return out;\n}\n";

        static GpuResourceLayoutDescription[] Layout(params GpuResourceKind[] kinds)
            => new[] { new GpuResourceLayoutDescription(Elements(kinds)) };

        static GpuResourceLayoutElement[] Elements(params GpuResourceKind[] kinds)
        {
            var elements = new GpuResourceLayoutElement[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
                elements[i] = new GpuResourceLayoutElement("e" + i, kinds[i], GpuShaderStages.Fragment);
            return elements;
        }
    }
}
