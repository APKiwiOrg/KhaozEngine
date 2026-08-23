using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The Metal binding-order guard, from both sides: every case is REAL SHIPPED SOURCE with one line moved or
    /// one expression dropped, and every one is proved to pass again the moment the perturbation is undone, so a
    /// guard that had started rejecting everything would fail here rather than look like a pass.
    /// <para>
    /// <b>HALF OF THESE FACTS INVERTED IN 18.0.0, AND THEY ARE REWRITTEN RATHER THAN DELETED.</b> Row 10 (#693)
    /// made the engine AUTHOR each resource's Metal index, walking the reflected layout in ascending
    /// <c>(set, binding)</c>, so a stage's emitted indices are in binding order by construction. That is exactly
    /// the property <c>MslBindingOrder.CheckStage</c> existed to enforce, and its premise (the cross-compiler
    /// numbers a stage's arguments in FIRST-REFERENCE order) is simply false now. The check is inert: it cannot
    /// fire on any input. So the three first-reference cases below assert that the perturbed source is now
    /// ACCEPTED, which is the honest record of what changed and is what makes the deletion in
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> a removal of dead code rather
    /// than a loosening.
    /// </para>
    /// <para>
    /// <b>THE PREFIX CASES ARE UNTOUCHED AND STILL LIVE.</b> <c>CheckPrefix</c> tests a property of the SHADER
    /// (which elements each stage reads) rather than of the numbering, so it still throws. It is the piece #604
    /// has to lift deliberately, in the same change that rewrites the shaders it blocks.
    /// </para>
    /// <para>
    /// These are plain <c>[Fact]</c>s. The whole check is a text read of a cross-compile, so it runs in the fast
    /// GPU-free lane on every push, which is the entire point: the three shipped bugs of this shape were each
    /// found by an image golden or a bisect, days later and on one backend only.
    /// </para>
    /// <para>
    /// Line endings are NORMALISED to LF and then run BOTH ways, exactly as
    /// <see cref="OceanFftShaderValidationTests"/> does and for the same reason: the sources are C# verbatim
    /// string literals, so a Windows checkout carries CRLF and a <c>"\n"</c> marker silently never matches.
    /// </para>
    /// </summary>
    public sealed class MslBindingOrderGuardTests
    {
        // ---- 1. The graphics pair, which had no guard at all before 17.36.0 -------------------------------

        /// <summary>
        /// THE FFT OCEAN'S OWN REGRESSION, INVERTED BY ROW 10 (issue #323, and the "One map array, bound first"
        /// section of the FFT ocean design). The water fragment reads the ocean map (binding 2) and then the
        /// resolved scene depth (binding 4). Lifting the scene-depth read above the cascade block used to swap
        /// the two on Metal, because the cross-compiler numbered a stage's textures by FIRST REFERENCE while the
        /// resource layout was counted in binding order, and the shipped symptom was the water reading its own
        /// derivative layer as the scene depth: it rendered, it just rendered wrong, on Metal only.
        /// <para>
        /// SINCE 18.0.0 THE INDEX IS AUTHORED IN BINDING ORDER, so moving a read moves nothing. The perturbation
        /// is still applied and still proved to change the source, because the value of this row now is that it
        /// says WHICH edit stopped mattering and why.
        /// </para>
        /// </summary>
        [Fact]
        public void TheWaterFragmentWithItsSceneDepthReadLifted_IsAcceptedBecauseTheIndexIsAuthored()
        {
            const string depthRead = "    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);\n";
            const string oceanBlock = "    vec2 oceanSlope = vec2(0.0);\n";

            string lf = ShaderSources.WaterFrag.Replace("\r\n", "\n");
            Assert.Contains(depthRead, lf);
            Assert.Contains(oceanBlock, lf);

            foreach (string source in new[] { lf, lf.Replace("\n", "\r\n") })
            {
                string nl = NewlineOf(source);
                string broken = source
                    .Replace(depthRead.Replace("\n", nl), "")
                    .Replace(oceanBlock.Replace("\n", nl), depthRead.Replace("\n", nl) + oceanBlock.Replace("\n", nl));
                Assert.NotEqual(source, broken);

                ShaderValidation.ValidatePair(
                    ShaderSources.WaterVert, broken, "WaterWithTheDepthReadLifted");
                ShaderValidation.ValidatePair(ShaderSources.WaterVert, source, "WaterUnmodified");
            }
        }

        /// <summary>
        /// THE PREFIX PROPERTY, which is a property of the LAYOUT and survives any amount of reordering inside
        /// the shader bodies. Drop the water VERTEX's bathymetry tap (binding 0) and the vertex is left reading
        /// the ocean map (binding 2) while the fragment still reads both, so the vertex's textures are no longer
        /// a prefix of the layout's: Veldrid bound the ocean map at texture index 1 for every stage, the vertex's
        /// emission numbers its one texture 0, and no binding number reconciles that.
        /// <para>
        /// The perturbation is one expression replaced by the fallback constant already sitting beside it, which
        /// is what a plausible "the depth field is off in the vertex" edit would look like.
        /// </para>
        /// </summary>
        [Fact]
        public void TheWaterVertexIsRejectedIfItStopsReadingTheBathymetryTheFragmentStillReads()
        {
            const string tap = "textureLod(sampler2D(BathyTex, BathySamp), buv, 0.0).r : KE_BATHY_DEEP;";
            const string dropped = "KE_BATHY_DEEP : KE_BATHY_DEEP;";

            string lf = ShaderSources.WaterVert.Replace("\r\n", "\n");
            Assert.Contains(tap, lf);

            string broken = lf.Replace(tap, dropped);
            Assert.NotEqual(lf, broken);

            var ex = Assert.Throws<ShaderValidationException>(
                () => ShaderValidation.ValidatePair(broken, ShaderSources.WaterFrag, "WaterWithoutTheVertexBathyTap"));
            Assert.Contains("WaterWithoutTheVertexBathyTap", ex.Message);
            Assert.Contains("the vertex stage's texture resources are not a PREFIX", ex.Message);
            Assert.Contains("reads layout(set=0, binding=2)", ex.Message);
            Assert.Contains("never reads layout(set=0, binding=0)", ex.Message);

            ShaderValidation.ValidatePair(lf, ShaderSources.WaterFrag, "WaterUnmodified");
        }

        /// <summary>
        /// AN ATTRIBUTED ENTRY POINT IS STILL READ. SPIRV-Cross emits a function attribute on the SAME LINE as
        /// the entry point it decorates, so a fragment declaring <c>layout(early_fragment_tests) in;</c> comes
        /// out as <c>[[ early_fragment_tests ]] fragment main0_out main0(...)</c>. A parse that accepts the stage
        /// keyword only at the start of a line loses that entry point, and since an entry point the parse cannot
        /// find is SILENCE rather than a throw, such a shader would validate clean with no checks at all.
        /// <para>
        /// THE PROOF IS A PREFIX VIOLATION SINCE 18.0.0, and it had to move. It used to be a reversed reference
        /// order, which row 10 made legal, so a green run would then have proved nothing about whether the entry
        /// point was found. A stage reading a LATER element while skipping an earlier one of the same kind is
        /// still a refusal, so it still separates "the guard saw this function" from "the guard saw nothing".
        /// </para>
        /// </summary>
        [Fact]
        public void AFragmentDeclaringEarlyFragmentTestsIsStillChecked()
        {
            const string skipsA = "texture(sampler2D(B, S), vec2(0.5))";
            const string readsBoth = "texture(sampler2D(A, S), vec2(0.5)) + texture(sampler2D(B, S), vec2(0.5))";
            const string vert = @"#version 450
layout(set=0, binding=0) uniform texture2D A;
layout(set=0, binding=1) uniform texture2D B;
layout(set=0, binding=2) uniform sampler S;
layout(location=0) in vec3 P;
void main() { gl_Position = vec4(P, 1.0) + textureLod(sampler2D(A, S), vec2(0.5), 0.0); }";
            const string frag = @"#version 450
layout(early_fragment_tests) in;
layout(set=0, binding=0) uniform texture2D A;
layout(set=0, binding=1) uniform texture2D B;
layout(set=0, binding=2) uniform sampler S;
layout(location=0) out vec4 o;
void main() { o = " + skipsA + @"; }";

            var ex = Assert.Throws<ShaderValidationException>(
                () => ShaderValidation.ValidatePair(vert, frag, "EarlyFragmentTestsSkippingA"));
            Assert.Contains("EarlyFragmentTestsSkippingA", ex.Message);
            Assert.Contains("the fragment stage's texture resources are not a PREFIX", ex.Message);
            Assert.Contains("reads layout(set=0, binding=1)", ex.Message);
            Assert.Contains("never reads layout(set=0, binding=0)", ex.Message);

            ShaderValidation.ValidatePair(
                vert, frag.Replace(skipsA, readsBoth), "EarlyFragmentTestsReadingBoth");
        }

        // ---- 2. The compute guard, which could not see a same-kind swap before 17.36.0 -------------------

        /// <summary>
        /// TWO STORAGE BUFFERS SWAPPING, THE COMPUTE HALF OF THE SAME INVERSION. Metal spells both
        /// <c>device T&amp;</c> and the reflection calls both <c>StructuredBufferReadWrite</c>, so nothing about
        /// their KINDS distinguishes them, which is why the kind-comparing guard was blind to this and the id
        /// join was not. The column pass reads the work buffer (binding 1) and then the foam accumulator
        /// (binding 2), and lifting the foam read above the work reads used to swap their Metal slots. The
        /// authored indices follow binding order, so it no longer does.
        /// </summary>
        [Fact]
        public void TheColumnPassWithItsFoamReadLifted_IsAcceptedBecauseTheIndexIsAuthored()
        {
            string lf = OceanComputeShaders.ColumnPass(32).Replace("\r\n", "\n");

            // Both are std430 storage buffers, which is precisely why the ORDER-OF-KINDS check cannot see this
            // swap and the SPIR-V id join can.
            Assert.Contains("layout(std430, set = 0, binding = 1) buffer WorkBuf", lf);
            Assert.Contains("layout(std430, set = 0, binding = 2) buffer FoamBuf", lf);

            const string foamRead = "    uint fiLo = ";
            const string workLoop = "    uint stride = ";
            int start = lf.IndexOf(foamRead, System.StringComparison.Ordinal);
            int end = lf.IndexOf("\n    Foam[fiLo]", System.StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            string block = lf[start..(end + 1)];

            foreach (string source in new[] { lf, lf.Replace("\n", "\r\n") })
            {
                string nl = NewlineOf(source);
                string moved = block.Replace("\n", nl);
                string broken = source.Replace(moved, "").Replace(workLoop, moved + workLoop);
                Assert.NotEqual(source, broken);

                ShaderValidation.ValidateCompute(broken, "ColumnPassWithTheFoamReadLifted");
                ShaderValidation.ValidateCompute(source, "ColumnPassUnmodified");
            }
        }

        // ---- 3. The false-positive budget ----------------------------------------------------------------

        /// <summary>
        /// A RESOURCE NEITHER STAGE REFERENCES must not trip anything. The cross-compiler drops it from the
        /// emission entirely and Veldrid reflected an unreferenced separate texture as a <c>UniformBuffer</c> with
        /// no stages, so counting it would be counting a phantom. The guard sees only what the emission kept,
        /// which makes this a clean pass rather than a false alarm (and, deliberately, a false NEGATIVE for the
        /// real slot the dead element still consumes).
        /// </summary>
        [Fact]
        public void ADeadStrippedResourceBetweenTwoLiveOnesValidatesClean()
        {
            const string frag = @"#version 450
layout(set=0, binding=0) uniform texture2D A;
layout(set=0, binding=1) uniform texture2D NeverRead;
layout(set=0, binding=2) uniform texture2D B;
layout(set=0, binding=3) uniform sampler S;
layout(location=0) out vec4 o;
void main() { o = texture(sampler2D(A, S), vec2(0.5)) + texture(sampler2D(B, S), vec2(0.5)); }";

            ShaderValidation.ValidatePair(PositionOnlyVert, frag, "DeadStripped");
        }

        /// <summary>
        /// A READ-ONLY STORAGE BUFFER BESIDE A TEXTURE in one graphics stage. Worth its own fact because the two
        /// share Direct3D11's <c>t</c> register class while living in DIFFERENT Metal index spaces, so any guard
        /// that reconstructed Metal's numbering from register counters would mis-order this pair. The id join
        /// reads each argument's own index space off its attribute and never has to.
        /// </summary>
        [Fact]
        public void AStorageBufferBesideATextureValidatesClean()
        {
            const string frag = @"#version 450
layout(std430, set=0, binding=0) readonly buffer Values { vec4 D[]; };
layout(set=0, binding=1) uniform texture2D T;
layout(set=0, binding=2) uniform sampler S;
layout(location=0) out vec4 o;
void main() { o = D[0] + texture(sampler2D(T, S), vec2(0.5)); }";

            ShaderValidation.ValidatePair(PositionOnlyVert, frag, "StorageBufferBesideTexture");
        }

        /// <summary>
        /// AN ARRAY OF TEXTURES never reaches the guard: the Metal cross-compile refuses it outright, so the
        /// emission the guard would have parsed does not exist. Pinned here because "the argument type is
        /// <c>array&lt;texture2d&lt;float&gt;, 4&gt;</c>" is otherwise a live false-positive risk in the argument
        /// parse, and this is the fact that says it cannot arise.
        /// </summary>
        [Fact]
        public void AnArrayOfTexturesIsRefusedByTheCrossCompilerRatherThanByTheGuard()
        {
            const string frag = @"#version 450
layout(set=0, binding=0) uniform texture2D T[3];
layout(set=0, binding=1) uniform sampler S;
layout(location=0) out vec4 o;
void main() { o = texture(sampler2D(T[1], S), vec2(0.5)) + texture(sampler2D(T[0], S), vec2(0.5)); }";

            var ex = Assert.Throws<ShaderValidationException>(
                () => ShaderValidation.ValidatePair(PositionOnlyVert, frag, "TextureArray"));
            Assert.Contains("cross-compile to MSL failed", ex.Message);
            Assert.DoesNotContain("binding order", ex.Message);
        }

        /// <summary>A stage that declares no resources at all is a prefix of anything, and the post stack's
        /// fullscreen vertex is exactly that shape. Guards the degenerate end of the prefix check.</summary>
        [Fact]
        public void AStageWithNoResourcesAtAllValidatesClean()
            => ShaderValidation.ValidatePair(PositionOnlyVert, @"#version 450
layout(set=0, binding=0) uniform texture2D T;
layout(set=0, binding=1) uniform sampler S;
layout(set=0, binding=2) uniform Block { vec4 Tint; };
layout(location=0) out vec4 o;
void main() { o = texture(sampler2D(T, S), vec2(0.5)) * Tint; }", "NoResourceVertex");

        const string PositionOnlyVert = @"#version 450
layout(location=0) in vec3 P;
void main() { gl_Position = vec4(P, 1.0); }";

        static string NewlineOf(string source) => source.Contains("\r\n") ? "\r\n" : "\n";
    }
}
