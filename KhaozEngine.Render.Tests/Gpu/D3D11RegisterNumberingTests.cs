using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE REGISTER-NUMBERING TABLE TEST (decision S2). The cross-compiled HLSL numbers its own registers and the
    /// CPU side has to agree exactly. When it does not, every shader still compiles, every draw still succeeds,
    /// and every pixel is wrong, which is a failure no other test in the suite can see: a golden would catch it
    /// only on the one backend that has a golden, and only after someone ran it.
    /// <para>
    /// IT COVERS EVERY LAYOUT THE RENDERERS DECLARE, not a hand-picked few. There are more than thirty
    /// <c>CreateResourceLayout</c> sites outside the seam package and the tests, and the table below transcribes
    /// all of them, named by their declaring type. The "six" figure that gets quoted is the count of DYNAMIC
    /// layout ELEMENTS, which is a different and much smaller set, and asserting only those would leave the entire
    /// texture-and-sampler register space unchecked. That space is exactly where a numbering error compiles
    /// cleanly and renders wrongly.
    /// </para>
    /// <para>
    /// EVERYTHING HERE IS DEVICE-FREE. The numbering is a pure function over engine types, so this is an ordinary
    /// <c>[Fact]</c> that runs on macOS and Linux as well as Windows, on every <c>dotnet test</c>. Nothing in this
    /// file names a Direct3D type.
    /// </para>
    /// <para>
    /// WHEN A ROW HERE FAILS, one of two things happened. Either the numbering changed, which is a defect unless
    /// the emitted HLSL changed with it, or a renderer's layout was edited. Reordering a layout's elements DOES
    /// legitimately renumber it, and the expected column is then updated in the same commit as the renderer
    /// change, which makes the diff the record of what moved. Silently editing this column to make a red build go
    /// green is how the whole class of defect ships.
    /// </para>
    /// </summary>
    public sealed class D3D11RegisterNumberingTests
    {
        // Stages do not participate in the numbering (asserted below), so the table uses one canonical stage mask
        // per kind rather than transcribing each site's, which keeps the rows readable as what they assert.
        static GpuResourceLayoutElement U(string name, bool dynamic = false)
            => new(name, GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic);
        static GpuResourceLayoutElement T(string name)
            => new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement S(string name)
            => new(name, GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement StructRO(string name)
            => new(name, GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute);
        static GpuResourceLayoutElement StructRW(string name)
            => new(name, GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute);
        static GpuResourceLayoutElement TextureRW(string name)
            => new(name, GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute);

        /// <summary>
        /// Every resource layout the engine's renderers declare, with the registers its elements must take. The
        /// expected column is the layout-RELATIVE numbering, in declaration order, written the way HLSL writes it.
        /// </summary>
        public static readonly (string Site, GpuResourceLayoutElement[] Elements, string Expected)[] Declared =
        {
            ("SpriteBatch._layout", new[] { T("Tex"), S("Samp") }, "t0 s0"),
            ("SpriteBatch._vpLayout", new[] { U("Vp", dynamic: true) }, "b0"),

            ("BeamRenderer._layout", new[] { U("U") }, "b0"),
            ("DepthLineRenderer._layout", new[] { U("U") }, "b0"),
            ("DistortionRenderer._layout", new[] { U("Frame"), T("DepthTex"), S("Samp") }, "b0 t0 s0"),
            ("GroundDecalRenderer._layout",
                new[] { T("DepthTex"), S("Samp"), U("Frame", dynamic: true), T("NormalTex") }, "t0 s0 b0 t1"),

            ("ModelRenderer._layout",
                new[] { U("U"), T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"), T("ShadowMap"),
                    S("ShadowSamp") },
                "b0 t0 t1 t2 s0 t3 s1"),
            // The only shipped layout with TWO uniform buffers in it, since #604 unfolded the skinned pipeline's
            // combined block into a shared frame block and a per-draw one.
            ("ModelRenderer._skinnedMainLayout", new[] { U("U"), U("VBlock", dynamic: true) }, "b0 b1"),
            ("ModelRenderer._skinnedFragLayout",
                new[] { T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"), T("ShadowMap"),
                    S("ShadowSamp") },
                "t0 t1 t2 s0 t3 s1"),
            // The splat pass's two, also since #604: the shared frame block on its own, then the material.
            ("ModelRenderer._splatFrameLayout", new[] { U("U") }, "b0"),
            ("ModelRenderer._splatMaterialLayout",
                new[] { U("SplatParams"), T("AlbedoArray"), T("NormalArray"), S("Sampler"), T("ShadowMap"),
                    S("ShadowSamp") },
                "b0 t0 t1 s0 t2 s1"),

            // The only two layouts in the engine that reach the u file at all, and the only ones that mix a
            // read-write structured buffer with a storage texture. They are why the u counter is SHARED.
            ("OceanFftProducer._rowLayout",
                new[] { U("Params"), StructRW("H0Buf"), StructRW("WorkBuf") }, "b0 u0 u1"),
            ("OceanFftProducer._colLayout",
                new[] { U("Params"), StructRW("WorkBuf"), StructRW("FoamBuf"), TextureRW("OceanMap") },
                "b0 u0 u1 u2"),

            ("OverlayMeshRenderer._layout", new[] { U("Draw", dynamic: true) }, "b0"),
            ("OverlayRenderer._layout", new[] { U("U") }, "b0"),
            ("ParticleRenderer._layout",
                new[] { U("Frame"), T("DepthTex"), S("Samp"), T("MotionTex"), T("AtlasTex"), S("AtlasSamp") },
                "b0 t0 s0 t1 t2 s1"),

            // The post chain declares nine, all with the uniform buffer LAST, which is the mirror image of the
            // model pass and the reason a rule of thumb like "the UBO is b0 and comes first" is worthless here.
            ("PixelPostProcess._palLayout", new[] { T("Src"), S("Samp"), U("Pal") }, "t0 s0 b0"),
            ("PixelPostProcess._edgeLayout",
                new[] { T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge") }, "t0 t1 t2 s0 b0"),
            ("PixelPostProcess._blitLayout", new[] { T("Src"), S("Samp"), U("Final") }, "t0 s0 b0"),
            ("PixelPostProcess._fxaaLayout", new[] { T("Src"), S("Samp"), U("Fxaa") }, "t0 s0 b0"),
            ("PixelPostProcess._brightLayout", new[] { T("Src"), S("Samp"), U("Bright") }, "t0 s0 b0"),
            ("PixelPostProcess._blurLayout", new[] { T("Src"), S("Samp"), U("Blur") }, "t0 s0 b0"),
            ("PixelPostProcess._compositeLayout",
                new[] { T("Src"), T("Bloom"), S("Samp"), U("Composite") }, "t0 t1 s0 b0"),
            ("PixelPostProcess._toneLayout", new[] { T("Src"), S("Samp"), U("Tone") }, "t0 s0 b0"),
            ("PixelPostProcess._applyLayout",
                new[] { T("Src"), T("OffsetTex"), S("Samp"), U("Apply") }, "t0 t1 s0 b0"),

            ("ShadowMapRenderer._layout", new[] { U("U", dynamic: true) }, "b0"),
            ("ShadowMapRenderer._skinnedLayout", new[] { U("VBlock", dynamic: true) }, "b0"),
            ("SkyRenderer._layout", new[] { U("Sky") }, "b0"),
            ("StarfieldRenderer._layout", new[] { U("Starfield") }, "b0"),
            ("TexturedBillboardRenderer._layout", new[] { U("U"), T("Tex"), S("Samp") }, "b0 t0 s0"),
            ("TrailRenderer._layout", new[] { U("U") }, "b0"),
            ("TransitionRenderer._solidLayout", new[] { U("Fill") }, "b0"),
            ("TransitionRenderer._crossLayout", new[] { T("Src"), S("Samp"), U("Params") }, "t0 s0 b0"),

            // The widest shipped layout, and the one whose own source comment already explains that the order is
            // load-bearing because of exactly this numbering.
            ("WaterRenderer._layout",
                new[] { T("BathyTex"), S("BathySamp"), T("OceanMap"), S("OceanSamp"), T("DepthTex"), S("Samp"),
                    U("Water", dynamic: true) },
                "t0 s0 t1 s1 t2 s2 b0"),
        };

        /// <summary>
        /// The table itself. One assertion over every row, reporting EVERY mismatch rather than the first, because
        /// a change to the numbering rule breaks many rows at once and seeing one of them tells you nothing about
        /// the shape of the break.
        /// </summary>
        [Fact]
        public void EveryDeclaredLayout_NumbersItsRegistersExactly()
        {
            var wrong = new List<string>();
            foreach ((string site, GpuResourceLayoutElement[] elements, string expected) in Declared)
            {
                string actual = Registers(elements);
                if (actual != expected) wrong.Add($"{site}: expected [{expected}] but got [{actual}]");
            }

            Assert.True(wrong.Count == 0,
                "The Direct3D 11 register assignment moved for " + wrong.Count + " declared layout(s):"
                + Environment.NewLine + string.Join(Environment.NewLine, wrong));
        }

        /// <summary>
        /// A guard on the table rather than on the code: the shapes it covers have to stay broad. A table that
        /// quietly shrank to the easy cases would keep passing while covering nothing, and the whole argument for
        /// this test is breadth. Distinct SHAPES, not row count, because two renderers legitimately declare the
        /// same shape.
        /// </summary>
        [Fact]
        public void TheTable_StaysBroaderThanTheEasyCases()
        {
            int distinctShapes = Declared.Select(r => r.Expected).Distinct(StringComparer.Ordinal).Count();

            Assert.True(Declared.Length >= 30,
                $"The table covers {Declared.Length} declaration sites and the engine has more than thirty.");
            Assert.True(distinctShapes >= 12,
                $"The table covers {distinctShapes} distinct register shapes, which is fewer than the dozen-plus "
                + "the renderers actually declare.");
            // The u file has exactly two shipped consumers, and losing them would leave the shared read-write
            // counter untested while everything else still passed.
            Assert.Contains(Declared, r => r.Expected.Contains('u', StringComparison.Ordinal));
        }

        /// <summary>
        /// The four kind-to-file mappings, stated on their own so a failure names the rule rather than a renderer.
        /// The two SHARING pairs are the content: a texture and a read-only structured buffer compete for the same
        /// <c>t</c> counter, and a storage texture and a read-write structured buffer for the same <c>u</c>.
        /// </summary>
        [Fact]
        public void TheTwoSharedCounters_AreSharedInDeclarationOrder()
        {
            Assert.Equal("t0 t1 t2 t3", Registers(T("a"), StructRO("b"), T("c"), StructRO("d")));
            Assert.Equal("u0 u1 u2 u3", Registers(TextureRW("a"), StructRW("b"), TextureRW("c"), StructRW("d")));
            // And the four files do NOT interleave: each counts from zero independently.
            Assert.Equal("b0 t0 s0 u0 b1 t1 s1 u1",
                Registers(U("b0"), T("t0"), S("s0"), StructRW("u0"), U("b1"), T("t1"), S("s1"), TextureRW("u1")));
        }

        /// <summary>
        /// Every <see cref="GpuResourceKind"/> has a decided register file. Enumerated rather than listed, so a
        /// kind appended to the seam fails here instead of reaching a shader with no register at all.
        /// </summary>
        [Fact]
        public void EveryResourceKind_MapsToARegisterFile()
        {
            foreach (GpuResourceKind kind in Enum.GetValues<GpuResourceKind>())
            {
                D3D11RegisterFile file = D3D11RegisterScheme.FileFor(kind);
                Assert.True(Enum.IsDefined(file), $"{kind} maps to an undefined register file.");
            }
        }

        /// <summary>
        /// Stages are not part of the numbering. Worth stating because it is a plausible wrong model: a reader
        /// could expect a fragment-only texture and a vertex-only texture to number separately, the way the
        /// cross-compiler numbers each STAGE densely over the bindings that stage declares. They do not, and the
        /// difference between the two models is precisely what <c>WaterRenderer</c>'s ordering comment is about.
        /// </summary>
        [Fact]
        public void ShaderStages_DoNotChangeTheNumbering()
        {
            var fragmentOnly = new[]
            {
                new GpuResourceLayoutElement("A", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("B", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            };
            var mixed = new[]
            {
                new GpuResourceLayoutElement("A", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex),
                new GpuResourceLayoutElement("B", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            };

            Assert.Equal(Registers(fragmentOnly), Registers(mixed));
        }

        /// <summary>
        /// The dynamic flag is not part of the numbering either, and is carried through untouched. It decides
        /// whether a per-draw byte offset is added at bind, which is a different question from which register the
        /// binding takes.
        /// </summary>
        [Fact]
        public void TheDynamicFlag_IsCarriedButDoesNotRenumber()
        {
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                U("Static"), U("Dynamic", dynamic: true)));

            Assert.Equal("b0 b1", Registers(U("Static"), U("Dynamic", dynamic: true)));
            Assert.False(layout.IsDynamic(0));
            Assert.True(layout.IsDynamic(1));
        }

        /// <summary>
        /// ACROSS layouts, the flattening follows the PIPELINE ARRAY, per file. Shown on the three shipped
        /// multi-layout pipelines: <c>SpriteBatch</c>, the skinned model pass and the splat pass.
        /// </summary>
        [Fact]
        public void AcrossLayouts_TheShippedPipelinesFlattenInArrayOrder()
        {
            // SpriteBatch: set 0 is the texture and sampler, set 1 is the view-projection UBO. The UBO is at
            // GLSL set = 1 deliberately, so "set 0 first" is already false in shipped code, and the array is what
            // decides.
            using var spriteTexture = new D3D11ResourceLayout(new GpuResourceLayoutDescription(T("Tex"), S("Samp")));
            using var spriteVp = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("Vp", dynamic: true)));
            D3D11ResourceLayout[] sprite = { spriteTexture, spriteVp };

            Assert.Equal("t0 s0", Absolute(sprite, 0));
            Assert.Equal("b0", Absolute(sprite, 1));

            // The skinned model pass: the shared frame block and the per-draw block at set 0, the material
            // textures and samplers at set 1.
            using var skinnedMain = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                U("U"), U("VBlock", dynamic: true)));
            using var skinnedFrag = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"), T("ShadowMap"), S("ShadowSamp")));
            D3D11ResourceLayout[] skinned = { skinnedMain, skinnedFrag };

            Assert.Equal("b0 b1", Absolute(skinned, 0));
            Assert.Equal("t0 t1 t2 s0 t3 s1", Absolute(skinned, 1));

            // The splat pass, which is where a shipped pipeline really does accumulate a base ACROSS its sets: set
            // 0 is the shared frame block at b0 and set 1's own params buffer lands at b1 because of it.
            using var splatFrame = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("U")));
            using var splatMaterial = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                U("SplatParams"), T("AlbedoArray"), T("NormalArray"), S("Sampler"), T("ShadowMap"), S("ShadowSamp")));
            D3D11ResourceLayout[] splat = { splatFrame, splatMaterial };

            Assert.Equal("b0", Absolute(splat, 0));
            Assert.Equal("b1 t0 t1 s0 t2 s1", Absolute(splat, 1));
        }

        /// <summary>
        /// The accumulation over MORE THAN TWO sets and over every register file at once, which no shipped
        /// pipeline exercises. The splat pass above is the one shipped case that accumulates a base at all (one
        /// file, two sets), and until #604 there was none: every multi-layout pipeline used disjoint kinds across
        /// its sets, so every base happened to be zero and a backend that ignored the base entirely would have
        /// passed every golden. This case stays synthetic on purpose, because it is still the only thing standing
        /// between "the bases are added, in all four files, at any depth" and a silent revert to zero.
        /// </summary>
        [Fact]
        public void AcrossLayouts_ABaseAccumulatesPerFile()
        {
            using var set0 = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                U("A"), T("X"), S("P"), StructRW("W")));
            using var set1 = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                U("B"), T("Y"), T("Z"), S("Q"), TextureRW("V")));
            using var set2 = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("C"), T("Zz")));
            D3D11ResourceLayout[] pipeline = { set0, set1, set2 };

            Assert.Equal("b0 t0 s0 u0", Absolute(pipeline, 0));
            Assert.Equal("b1 t1 t2 s1 u1", Absolute(pipeline, 1));
            Assert.Equal("b2 t3", Absolute(pipeline, 2));
        }

        /// <summary>
        /// The base comes from the ARRAY position and nothing else, so THE SAME layout object bound at a different
        /// slot numbers differently. That is what makes the layout's own stored assignment relative, and it is why
        /// a layout can be shared between pipelines without being renumbered per pipeline.
        /// </summary>
        [Fact]
        public void OneLayout_NumbersDifferentlyAtADifferentSlot()
        {
            using var shared = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("U"), T("Tex")));
            using var other = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("Other"), T("OtherTex")));

            D3D11ResourceLayout[] sharedFirst = { shared, other };
            D3D11ResourceLayout[] sharedSecond = { other, shared };

            Assert.Equal("b0 t0", Absolute(sharedFirst, 0));
            Assert.Equal("b1 t1", Absolute(sharedSecond, 1));
        }

        /// <summary>
        /// A set index past the pipeline's layout array is a pipeline and set mismatch, not an empty base. Left
        /// to fall through it would bind every register at zero and render the wrong resources. The BOUNDARY
        /// index, <c>Length</c> itself, is the case that matters and is asserted first: it is the first invalid
        /// slot, and it is the one a fall-through answers with the sum over every layout in the pipeline, which
        /// is a plausible-looking base rather than an obvious zero.
        /// </summary>
        [Fact]
        public void ASetSlotPastThePipelineArray_Throws()
        {
            using var only = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("U")));
            using var second = new D3D11ResourceLayout(new GpuResourceLayoutDescription(T("Tex")));
            D3D11ResourceLayout[] one = { only };
            D3D11ResourceLayout[] two = { only, second };

            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11RegisterScheme.BaseFor(one, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11RegisterScheme.BaseFor(two, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11RegisterScheme.BaseFor(one, 2));
        }

        /// <summary>
        /// The other side of that boundary: <c>Length - 1</c> is the LAST valid slot and accumulates every layout
        /// before it. The two tests are only worth anything together, because a guard off by one in either
        /// direction still satisfies one of them alone.
        /// </summary>
        [Fact]
        public void TheLastSetSlot_IsInRangeAndAccumulatesTheLayoutsBeforeIt()
        {
            using var first = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("A"), T("X"), S("P")));
            using var last = new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("B"), T("Y")));
            D3D11ResourceLayout[] pipeline = { first, last };

            D3D11RegisterCounts baseCounts = D3D11RegisterScheme.BaseFor(pipeline, 1);

            Assert.Equal(1u, baseCounts.For(D3D11RegisterFile.ConstantBuffer));
            Assert.Equal(1u, baseCounts.For(D3D11RegisterFile.ShaderResource));
            Assert.Equal(1u, baseCounts.For(D3D11RegisterFile.Sampler));
            Assert.Equal(0u, baseCounts.For(D3D11RegisterFile.UnorderedAccess));
        }

        /// <summary>
        /// A DYNAMIC STRUCTURED ELEMENT IS REFUSED AT LAYOUT CREATION, both kinds, which is the second
        /// backend-divergent creation failure after decision U3's ring combination. Nothing further down the path
        /// would ever say so: a dynamic offset can only be carried by the constant-buffer bind, so a full
        /// activation writes the structured view with no offset added and the offsets-only path skips the element
        /// entirely for not being a constant buffer. Both halves of the flush silently agree to ignore it, and
        /// every draw reads the window the view was created with while the caller believes it moved.
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.StructuredBufferReadOnly)]
        [InlineData(GpuResourceKind.StructuredBufferReadWrite)]
        public void ADynamicStructuredElement_IsRefusedAtLayoutCreation(GpuResourceKind kind)
        {
            var element = new GpuResourceLayoutElement("Work", kind, GpuShaderStages.Compute, dynamic: true);

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new D3D11ResourceLayout(new GpuResourceLayoutDescription(U("Frame"), element)));

            Assert.Contains("Work", error.Message, StringComparison.Ordinal);
            Assert.Contains("dynamic", error.Message, StringComparison.Ordinal);
        }

        /// <summary>The same element without the dynamic flag is ordinary and numbers as the table above says, so
        /// the refusal is about the COMBINATION and does not cost the shipped compute layouts anything.</summary>
        [Fact]
        public void ANonDynamicStructuredElement_IsStillAccepted()
        {
            Assert.Equal("b0 t0 u0", Registers(U("Frame"), StructRO("In"), StructRW("Out")));
        }

        static string Registers(params GpuResourceLayoutElement[] elements)
        {
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(elements));
            var parts = new string[layout.ElementCount];
            for (int i = 0; i < parts.Length; i++) parts[i] = layout.SlotAt(i).ToString();
            return string.Join(' ', parts);
        }

        static string Absolute(D3D11ResourceLayout[] pipeline, uint setIndex)
        {
            D3D11RegisterCounts baseCounts = D3D11RegisterScheme.BaseFor(pipeline, setIndex);
            D3D11ResourceLayout layout = pipeline[setIndex];
            var parts = new string[layout.ElementCount];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = D3D11RegisterScheme.Absolute(baseCounts, layout.SlotAt(i)).ToString();
            return string.Join(' ', parts);
        }
    }
}
