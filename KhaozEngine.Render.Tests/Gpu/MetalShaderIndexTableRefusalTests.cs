using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// PIN 1 OF SECTION 2.2b, DRIVEN: <b>the parse fails LOUDLY and never falls back.</b> All SEVEN ways it can
    /// fail throw at shader-set creation, device-free, naming the program, the stage and the offending argument.
    /// Five of them are the id-keyed join in <c>MetalShaderIndexTable</c>, and two are the argument parse in front
    /// of it (<c>MetalMslEntryPoint</c>), where a malformed index attribute is a stop rather than a dropped
    /// argument.
    ///
    /// <para>
    /// WHY THIS IS THE MOST IMPORTANT FILE IN THE ROW. The <c>_&lt;id&gt;</c> argument name is a SPIRV-Cross
    /// emission convention and nothing promises it: a resource that reaches the emission carrying a real name, or
    /// a name mangled to dodge a collision, does not parse. 2.2b calls that the mechanism's genuine fragility and
    /// rules that the answer is to throw rather than to fall back to a count. A silent fallback would reintroduce
    /// the incumbent's failure mode INSIDE the mechanism that replaced it, which is the worst of the three
    /// outcomes available, and it is exactly the kind of "helpful" recovery a later reader adds in good faith.
    /// These rows are what make that edit fail.
    /// </para>
    /// <para>
    /// HAND-BUILT INPUTS RATHER THAN SHIPPED SHADERS, deliberately. Every shipped program joins cleanly (that is
    /// <see cref="MetalShaderIndexTableTests"/>'s subject), so a refusal can only be reached by constructing the
    /// shape, and constructing it is also the clearest statement of what each refusal MEANS. The SPIR-V is
    /// hand-assembled to a few instructions, because <c>SpirvResourceDecorations</c> needs nothing but
    /// <c>OpDecorate</c> and the module header.
    /// </para>
    /// </summary>
    public sealed class MetalShaderIndexTableRefusalTests
    {
        [Fact]
        public void AnArgumentNameThatIsNotAnId_IsRefusedRatherThanCounted()
        {
            ShaderValidationException error = Build(
                Spirv((Id: 70, Set: 0, Binding: 0)),
                new MetalMslArgument(MetalIndexSpace.Buffer, 0, "myUniformBlock"),
                Layout(GpuResourceKind.UniformBuffer));

            Assert.Contains("myUniformBlock", error.Message, StringComparison.Ordinal);
            Assert.Contains("_<id>", error.Message, StringComparison.Ordinal);

            // The point is not the message, it is that there is NO index. A fallback to the argument's ordinal
            // would have produced 0 here and bound something.
            Assert.Contains("no fallback", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnIdWithNoDecorationsInThisStagesModule_IsRefused()
        {
            // The module decorates 70 and the argument names 77, which is precisely what reading a PAIR'S shared
            // reflection instead of this stage's own module looks like (pin 2): each stage renumbers its ids, so
            // Model's vertex stage emits _70 where its fragment stage emits _77 for the same element.
            ShaderValidationException error = Build(
                Spirv((Id: 70, Set: 0, Binding: 0)),
                new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_77"),
                Layout(GpuResourceKind.UniformBuffer));

            Assert.Contains("77", error.Message, StringComparison.Ordinal);
            Assert.Contains("THIS STAGE'S own module", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ASetPastTheDeclaredLayoutArray_IsRefused()
        {
            ShaderValidationException error = Build(
                Spirv((Id: 70, Set: 3, Binding: 0)),
                new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_70"),
                Layout(GpuResourceKind.UniformBuffer));

            Assert.Contains("set 3", error.Message, StringComparison.Ordinal);
            Assert.Contains("positional assumption", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ABindingPastThatSetsElementArray_IsRefused()
        {
            ShaderValidationException error = Build(
                Spirv((Id: 70, Set: 0, Binding: 5)),
                new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_70"),
                Layout(GpuResourceKind.UniformBuffer));

            Assert.Contains("binding 5", error.Message, StringComparison.Ordinal);
            Assert.Contains("element M", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AKindThatDoesNotMatchItsIndexSpace_IsRefused()
        {
            // A texture element reached through a [[buffer(n)]] argument: the join landed somewhere, and the kind
            // check is what says it landed on the wrong element rather than binding a texture as a buffer.
            ShaderValidationException error = Build(
                Spirv((Id: 70, Set: 0, Binding: 0)),
                new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_70"),
                Layout(GpuResourceKind.TextureReadOnly));

            Assert.Contains("TextureReadOnly", error.Message, StringComparison.Ordinal);
            Assert.Contains("buffer index space", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TwoArgumentsResolvingToOneElementInOneStage_IsRefused()
        {
            // Two ids both decorated (0, 0). One of them would never be bound, so the bijection is asserted
            // rather than left to whichever write landed last.
            ShaderValidationException error = Build(
                Spirv((Id: 70, Set: 0, Binding: 0), (Id: 71, Set: 0, Binding: 0)),
                new[]
                {
                    new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_70"),
                    new MetalMslArgument(MetalIndexSpace.Buffer, 1, "_71"),
                },
                Layout(GpuResourceKind.UniformBuffer));

            Assert.Contains("bijection", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// REFUSAL SIX, IN THE PARSE RATHER THAN THE JOIN: an index attribute that never closes. The join cannot
        /// refuse an argument it never receives, so dropping this one would leave its element absent from the
        /// table, reading as unreferenced by the stage and therefore not bound. That is the black-frame-no-error
        /// outcome the whole row exists to close, so it is a stop here.
        /// </summary>
        [Fact]
        public void AnIndexAttributeThatNeverCloses_IsRefusedRatherThanDropped()
        {
            // The attribute's ')' lands past a comma, so the argument the parse sees carries an unterminated
            // [[buffer(. The entry point's own parentheses still balance, which is what keeps this the attribute's
            // refusal rather than the argument list's.
            ShaderValidationException error = ParseRefusal("constant _68& _70 [[buffer(0, 1)]]");

            Assert.Contains("_70", error.Message, StringComparison.Ordinal);
            Assert.Contains("never closes", error.Message, StringComparison.Ordinal);
            Assert.Contains("not bound", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// REFUSAL SEVEN: an index that is not a number. Unreachable from any shipped emission, because
        /// SPIRV-Cross writes a decimal literal, and that is exactly why it has to throw: the day it is reachable
        /// is the day the emission changed shape, and a skip would answer that with a silently unbound element.
        /// </summary>
        [Fact]
        public void AnIndexThatIsNotANumber_IsRefusedRatherThanDropped()
        {
            ShaderValidationException error = ParseRefusal("texture2d<float> _77 [[texture(zero)]]");

            Assert.Contains("_77", error.Message, StringComparison.Ordinal);
            Assert.Contains("zero", error.Message, StringComparison.Ordinal);
            Assert.Contains("no path that skips", error.Message, StringComparison.Ordinal);
        }

        /// <summary>The other half of those two: a WELL-FORMED entry point of the same shape parses, so neither
        /// row above is passing against a parse that throws unconditionally. It also pins what the parse reads,
        /// which is the name past the reference punctuation and the index out of the attribute.</summary>
        [Fact]
        public void AWellFormedEntryPoint_ParsesItsNameAndItsArgumentIndices()
        {
            (string name, List<MetalMslArgument> arguments) = MetalMslEntryPoint.Parse(
                EntryPoint("constant _68& _70 [[buffer(2)]]", "texture2d<float> _77 [[texture(0)]]"),
                MetalShaderStage.Fragment, "hand-built");

            Assert.Equal("main0", name);
            Assert.Equal(
                new[]
                {
                    new MetalMslArgument(MetalIndexSpace.Buffer, 2, "_70"),
                    new MetalMslArgument(MetalIndexSpace.Texture, 0, "_77"),
                },
                arguments);
        }

        /// <summary>The other half of pin 1: a CLEAN join over the same hand-built shape produces the index the
        /// emission chose, not the binding number. Without this the rows above would all pass against a Build
        /// that threw unconditionally.</summary>
        [Fact]
        public void ACleanJoin_RecordsTheIndexTheEmissionChoseRatherThanTheBindingNumber()
        {
            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly),
                new[]
                {
                    new MetalMslStageJoin(MetalShaderStage.Fragment,
                        Spirv((Id: 70, Set: 0, Binding: 0), (Id: 71, Set: 0, Binding: 1)),
                        new[]
                        {
                            // Binding 0 landed at buffer 2, binding 1 at texture 0: neither index is its binding.
                            new MetalMslArgument(MetalIndexSpace.Buffer, 2, "_70"),
                            new MetalMslArgument(MetalIndexSpace.Texture, 0, "_71"),
                        }),
                },
                "hand-built");

            Assert.True(table.TryGetIndex(0, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry uniform));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Buffer, 2), uniform);

            Assert.True(table.TryGetIndex(0, 1, MetalShaderStage.Fragment, out MetalIndexTableEntry texture));
            Assert.Equal(new MetalIndexTableEntry(MetalIndexSpace.Texture, 0), texture);

            // AND A STAGE WITH NO ENTRY IS NOT BOUND FOR THAT STAGE, which is correct by construction rather than
            // a gap: SPIRV-Cross omits an argument a stage does not reference.
            Assert.False(table.TryGetIndex(0, 0, MetalShaderStage.Vertex, out _));
        }

        /// <summary>
        /// PIN 4, DRIVEN: the declared layout array is shape-checked against the reflection the table was built
        /// from, at pipeline creation. Row 11 is the only caller, and this is what says the check is real before
        /// that row exists to call it.
        /// </summary>
        [Fact]
        public void ADeclaredLayoutArrayOfADifferentShape_IsRefusedByTheShapeCheck()
        {
            MetalShaderIndexTable table = MetalShaderIndexTable.Build(
                Layout(GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly),
                new[]
                {
                    new MetalMslStageJoin(MetalShaderStage.Fragment,
                        Spirv((Id: 70, Set: 0, Binding: 0)),
                        new[] { new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_70") }),
                },
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

        static ShaderValidationException Build(byte[] spirv, MetalMslArgument argument,
            GpuResourceLayoutDescription[] layouts)
            => Build(spirv, new[] { argument }, layouts);

        static ShaderValidationException Build(byte[] spirv, IReadOnlyList<MetalMslArgument> arguments,
            GpuResourceLayoutDescription[] layouts)
            => Assert.Throws<ShaderValidationException>(() => MetalShaderIndexTable.Build(
                layouts,
                new[] { new MetalMslStageJoin(MetalShaderStage.Fragment, spirv, arguments) },
                "hand-built"));

        static string Refusal(Action call) => Assert.Throws<ShaderValidationException>(call).Message;

        static ShaderValidationException ParseRefusal(params string[] arguments)
            => Assert.Throws<ShaderValidationException>(() => MetalMslEntryPoint.Parse(
                EntryPoint(arguments), MetalShaderStage.Fragment, "hand-built"));

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

        /// <summary>
        /// A minimal SPIR-V module carrying nothing but the header and the <c>OpDecorate</c> pairs asked for. That
        /// is genuinely all <c>SpirvResourceDecorations</c> reads, which is itself a fact worth pinning: the walk
        /// never resolves a storage class or chases a pointer type, so a module with no types, no functions and no
        /// entry point is a valid input to it.
        /// </summary>
        static byte[] Spirv(params (int Id, int Set, int Binding)[] resources)
        {
            const uint opDecorate = 71, decorationBinding = 33, decorationDescriptorSet = 34;
            var words = new List<uint> { 0x07230203, 0x00010000, 0, 1, 0 };   // magic, version, generator, bound, schema

            foreach ((int id, int set, int binding) in resources)
            {
                // OpDecorate is 4 words here: (wordCount << 16) | opcode, target, decoration, literal.
                words.AddRange(new[] { (4u << 16) | opDecorate, (uint)id, decorationDescriptorSet, (uint)set });
                words.AddRange(new[] { (4u << 16) | opDecorate, (uint)id, decorationBinding, (uint)binding });
            }

            var bytes = new byte[words.Count * 4];
            for (int w = 0; w < words.Count; w++)
            {
                uint value = words[w];
                bytes[w * 4] = (byte)value;
                bytes[w * 4 + 1] = (byte)(value >> 8);
                bytes[w * 4 + 2] = (byte)(value >> 16);
                bytes[w * 4 + 3] = (byte)(value >> 24);
            }
            return bytes;
        }
    }
}
