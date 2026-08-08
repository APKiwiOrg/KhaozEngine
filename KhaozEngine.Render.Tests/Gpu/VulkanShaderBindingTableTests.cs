using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-S8: the <c>set</c> and <c>binding</c> numbers the shipped GLSL declares are the INDICES the
    /// backend binds at. Section 12.2 of <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>,
    /// work-breakdown row 16 (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>WHY THIS IS WORTH A TEST WHEN THE NUMBERING IS INHERITED RATHER THAN INVENTED.</b> The Direct3D 11
    /// backend had to invent a register scheme and then prove the CPU side and the emitted HLSL agreed. Vulkan has
    /// no such freedom: the sources already say <c>layout(set = N, binding = M)</c>, and the backend's whole job is
    /// to make N the layout's index in the pipeline's layout array and M the element's index in that layout. That
    /// leaves no arithmetic to get wrong and exactly one thing: whether the two arrays are in the order the shaders
    /// assume. Get that wrong and everything compiles, every descriptor writes, every draw issues, and every pixel
    /// is wrong.</para>
    ///
    /// <para><b>ONE SHIPPED CASE MAKES IT WORTH DOING RATHER THAN ASSUMING.</b> <c>SpriteBatch</c> declares its
    /// uniform block at <c>set = 1</c> with its texture and sampler at <c>set = 0</c>, so "the UBO set comes first"
    /// is FALSE in shipped code, and a layout array reordered by a well-meaning refactor would be caught by nothing
    /// else in the net.</para>
    ///
    /// <para><b>IT READS THE REAL SOURCES.</b> The declarations are parsed out of the shipped GLSL constants
    /// themselves rather than transcribed, so a shader edit that moves a binding cannot pass by agreeing with a
    /// copy of itself. The other side, the pipeline layout arrays, IS transcribed, in
    /// <see cref="VulkanDescriptorLimitTests.ShippedLayouts"/> and
    /// <see cref="VulkanDescriptorLimitTests.ShippedPipelines"/>, because reading it off the real renderers needs a
    /// device. One transcribed side and one real side is what makes the comparison mean something.</para>
    ///
    /// <para><b>NAMES ARE DELIBERATELY NOT COMPARED.</b> The GLSL's declared name and the layout element's name
    /// disagree in shipped code (the model fragment's <c>sampler Samp</c> is declared <c>"Sampler"</c> in its
    /// layout), and Vulkan binds by NUMBER: names are not in the set-layout content key either, for the same
    /// reason. What is compared is the index grid and the resource KIND at each index, which is what a
    /// misordered array actually breaks.</para>
    /// </summary>
    public sealed class VulkanShaderBindingTableTests
    {
        /// <summary>
        /// EVERY SHIPPED PROGRAM AND THE PIPELINE IT IS BUILT INTO, which is the join the two halves need: the
        /// catalog names programs by their SOURCES and the limit table names pipelines by their RENDERER. Several
        /// programs share one pipeline shape, which is why this is a many-to-one map rather than a zip.
        /// </summary>
        static readonly IReadOnlyDictionary<string, string> ProgramPipelines =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Sprite2D"] = "SpriteBatch",

                ["Model"] = "ModelRenderer",
                ["ModelDissolve"] = "ModelRenderer dissolve",
                ["SkinnedModel"] = "ModelRenderer skinned",
                ["SkinnedModelDissolve"] = "ModelRenderer skinned dissolve",
                ["Splat"] = "ModelRenderer splat",

                // All three depth variants are built into the same single-set pipeline: the dissolve fragment
                // shaders read the instance's dissolve amount out of the SAME uniform block rather than adding a
                // resource of their own.
                ["ShadowDepth"] = "ShadowMapRenderer depth",
                ["ShadowDepthDissolve"] = "ShadowMapRenderer depth",
                ["ShadowDepthDissolveInverted"] = "ShadowMapRenderer depth",
                ["SkinnedShadowDepth"] = "ShadowMapRenderer skinned depth",

                ["Beam"] = "BeamRenderer",
                // The Line pair has three call sites and two of them go through OverlayRenderer, whose layout is
                // the same single uniform block. Its primary one is named here, as in the catalog.
                ["Line"] = "DepthLineRenderer",
                ["Billboard"] = "OverlayRenderer",
                ["TexturedBillboard"] = "TexturedBillboardRenderer",
                ["Particle"] = "ParticleRenderer",
                ["Trail"] = "TrailRenderer",
                ["Distortion"] = "DistortionRenderer",
                ["GroundDecal"] = "GroundDecalRenderer",
                ["OverlayMesh"] = "OverlayMeshRenderer",

                ["Sky"] = "SkyRenderer",
                ["Starfield"] = "StarfieldRenderer",
                ["Water"] = "WaterRenderer",
                ["WaterClipmap"] = "WaterRenderer",

                ["PostPalette"] = "PixelPostProcess pal",
                ["PostEdge"] = "PixelPostProcess edge",
                ["PostBlit"] = "PixelPostProcess blit",
                ["PostFxaa"] = "PixelPostProcess fxaa",
                ["PostTonemap"] = "PixelPostProcess tone",
                ["PostDistortionApply"] = "PixelPostProcess apply",
                ["PostBloomBright"] = "PixelPostProcess bright",
                ["PostBloomBlur"] = "PixelPostProcess blur",
                ["PostBloomComposite"] = "PixelPostProcess composite",

                ["TransitionSolid"] = "TransitionRenderer solid",
                ["TransitionCrossfade"] = "TransitionRenderer cross",
            };

        /// <summary>
        /// EVERY DECLARED <c>(set, binding)</c> IS A REAL INDEX PAIR, and the kind at it matches. This is the
        /// assertion the decision asks for: N indexes the pipeline's layout array and M indexes that layout's
        /// element array.
        /// </summary>
        [Fact]
        public void EveryShippedGraphicsProgram_DeclaresSetAndBindingAtTheArrayIndices()
        {
            var problems = new List<string>();

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                string[] slots = SlotsOf(program.Name, problems);
                if (slots.Length == 0) continue;

                foreach (Declaration d in Parse(program.VertexGlsl).Concat(Parse(program.FragmentGlsl)))
                    Check(program.Name, slots, d, problems);
            }

            Assert.True(problems.Count == 0, Report(problems));
        }

        /// <summary>The compute half, over all four cascade resolutions of both ocean kernels.</summary>
        [Fact]
        public void EveryShippedComputeKernel_DeclaresSetAndBindingAtTheArrayIndices()
        {
            var problems = new List<string>();

            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                string[] slots = SlotsOf(PipelineOf(kernel.Name), problems, kernel.Name);
                if (slots.Length == 0) continue;

                foreach (Declaration d in Parse(kernel.ComputeGlsl)) Check(kernel.Name, slots, d, problems);
            }

            Assert.True(problems.Count == 0, Report(problems));
        }

        /// <summary>
        /// AND THE DECLARATIONS COVER THE LAYOUTS COMPLETELY, which is the half the per-declaration check above
        /// cannot see. A layout element nothing declares is a descriptor the engine writes and no stage reads,
        /// which is either a dead binding or, far worse, an array that has drifted by one and happens to still be
        /// in range at every index a shader names.
        /// </summary>
        [Fact]
        public void EveryLayoutElementOfEveryShippedPipeline_IsDeclaredBySomeStageOfItsProgram()
        {
            var problems = new List<string>();

            foreach ((string program, string[] slots, IEnumerable<Declaration> declarations) in EveryProgram())
            {
                var declared = declarations.Select(d => (d.Set, d.Binding)).ToHashSet();

                for (int set = 0; set < slots.Length; set++)
                {
                    GpuResourceLayoutElement[] elements =
                        VulkanDescriptorLimitTests.ShippedLayouts[slots[set]].Elements ?? [];

                    for (int binding = 0; binding < elements.Length; binding++)
                    {
                        if (declared.Contains((set, binding))) continue;

                        problems.Add($"{program}: layout slot {set} ({slots[set]}) element {binding} "
                            + $"({elements[binding].Name}, {elements[binding].Kind}) is declared by no stage of "
                            + "the program. Either the pipeline's layout array carries a resource nothing reads, "
                            + "or it has drifted out of step with the shader sources.");
                    }
                }
            }

            Assert.True(problems.Count == 0, Report(problems));
        }

        /// <summary>
        /// THE MAP ITSELF IS COMPLETE AND NAMES REAL PIPELINES, which is what stops the three tests above from
        /// passing by covering nothing. A program with no entry is skipped by every one of them, so the omission
        /// has to fail here or it fails nowhere.
        /// </summary>
        [Fact]
        public void EveryShippedProgram_HasAPipelineAndEveryPipelineNamedIsReal()
        {
            string[] catalog = D3D11ShaderProgramCatalog.GraphicsPrograms().Select(p => p.Name).ToArray();
            var pipelines = VulkanDescriptorLimitTests.ShippedPipelines
                .Select(p => p.Pipeline)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(34, catalog.Length);
            Assert.Equal(catalog.Length, ProgramPipelines.Count);

            foreach (string program in catalog)
            {
                Assert.True(ProgramPipelines.ContainsKey(program),
                    $"{program} is in the shipped catalog and has no pipeline in this map, so every assertion in "
                    + "this file silently skips it.");
            }
            foreach (string pipeline in ProgramPipelines.Values.Concat(["OceanFftProducer row",
                         "OceanFftProducer col"]))
            {
                Assert.True(pipelines.Contains(pipeline),
                    $"This map names the pipeline {pipeline}, which VulkanDescriptorLimitTests.ShippedPipelines "
                    + "does not have. A renamed pipeline has to be renamed here too.");
            }
        }

        /// <summary>
        /// THE PARSER READS A MEMORY QUALIFIER ON EITHER SIDE OF THE STORAGE KEYWORD, asserted over a synthetic
        /// source because nothing shipped is spelled the canonical way yet. That is precisely why it earns a
        /// test: GLSL's canonical placement is <c>layout(...) readonly buffer B</c>, the pattern here used to
        /// demand the storage keyword immediately after the layout group and skipped that spelling whole, and a
        /// skipped declaration surfaces through
        /// <see cref="EveryLayoutElementOfEveryShippedPipeline_IsDeclaredBySomeStageOfItsProgram"/> as an
        /// undeclared LAYOUT ELEMENT, which reads as a pipeline bug rather than a parser one.
        /// </summary>
        [Fact]
        public void TheParser_ReadsAMemoryQualifierOnEitherSideOfTheStorageKeyword()
        {
            const string glsl = """
                layout(std430, set = 0, binding = 0) readonly buffer Before { vec4 a[]; };
                layout(std430, set = 0, binding = 1) buffer readonly After { vec4 b[]; };
                layout(std430, set = 0, binding = 2) buffer Writable { vec4 c[]; };
                layout(set = 1, binding = 0) uniform texture2D Tex;
                layout(rgba16f, set = 1, binding = 1) uniform readonly image2D Img;
                """;

            Declaration[] parsed = Parse(glsl).ToArray();

            Assert.Equal(5, parsed.Length);
            Assert.Equal(new Declaration(0, 0, GpuResourceKind.StructuredBufferReadOnly, "Before"), parsed[0]);
            Assert.Equal(new Declaration(0, 1, GpuResourceKind.StructuredBufferReadOnly, "After"), parsed[1]);
            Assert.Equal(new Declaration(0, 2, GpuResourceKind.StructuredBufferReadWrite, "Writable"), parsed[2]);
            Assert.Equal(new Declaration(1, 0, GpuResourceKind.TextureReadOnly, "Tex"), parsed[3]);

            // A readonly storage image stays the read-write kind on purpose: Vulkan gives it the same descriptor
            // type as a writable one, and only the buffer case maps to a different engine kind.
            Assert.Equal(new Declaration(1, 1, GpuResourceKind.TextureReadWrite, "Img"), parsed[4]);
        }

        // ---- the join ------------------------------------------------------------------------------------

        static IEnumerable<(string Program, string[] Slots, IEnumerable<Declaration> Declarations)> EveryProgram()
        {
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                yield return (program.Name, SlotsOf(program.Name, null),
                    Parse(program.VertexGlsl).Concat(Parse(program.FragmentGlsl)));
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
                yield return (kernel.Name, SlotsOf(PipelineOf(kernel.Name), null), Parse(kernel.ComputeGlsl));
        }

        // The ocean kernels are compiled per cascade resolution, so the four names of each pass share one
        // pipeline shape. Matching on the pass rather than listing eight rows keeps this from needing an edit
        // every time OceanResolutions changes.
        static string PipelineOf(string kernelName)
            => kernelName.StartsWith("OceanFftRowPass", StringComparison.Ordinal)
                ? "OceanFftProducer row"
                : "OceanFftProducer col";

        static string[] SlotsOf(string program, List<string>? problems, string? reportAs = null)
        {
            string pipeline = ProgramPipelines.TryGetValue(program, out string? mapped) ? mapped : program;

            foreach ((string name, string[] slots) in VulkanDescriptorLimitTests.ShippedPipelines)
            {
                if (string.Equals(name, pipeline, StringComparison.Ordinal)) return slots;
            }

            problems?.Add($"{reportAs ?? program}: no shipped pipeline named {pipeline}.");
            return [];
        }

        static void Check(string program, string[] slots, in Declaration d, List<string> problems)
        {
            if (d.Set >= slots.Length)
            {
                problems.Add($"{program}: declares set = {d.Set} for {d.Name}, and its pipeline has "
                    + $"{slots.Length} layout slot(s). The set number IS the index into the pipeline's layout "
                    + "array, so this shader cannot be bound at all.");
                return;
            }

            GpuResourceLayoutElement[] elements =
                VulkanDescriptorLimitTests.ShippedLayouts[slots[d.Set]].Elements ?? [];

            if (d.Binding >= elements.Length)
            {
                problems.Add($"{program}: declares binding = {d.Binding} for {d.Name} in set {d.Set} "
                    + $"({slots[d.Set]}), which has {elements.Length} element(s). The binding number IS the index "
                    + "into the layout's element array.");
                return;
            }

            if (elements[d.Binding].Kind != d.Kind)
            {
                problems.Add($"{program}: declares {d.Name} at set {d.Set} binding {d.Binding} as {d.Kind}, and "
                    + $"{slots[d.Set]}'s element {d.Binding} ({elements[d.Binding].Name}) is "
                    + $"{elements[d.Binding].Kind}. Everything compiles and every pixel is wrong when these "
                    + "disagree, because the descriptor is written as one kind and read as another.");
            }
        }

        static string Report(List<string> problems)
            => "The shipped GLSL's set and binding numbers do not match the pipeline layout arrays they index "
                + "(decision V-S8). Vulkan inherits this numbering rather than inventing one, so a mismatch is a "
                + "reordered layout array rather than a backend bug:\n  " + string.Join("\n  ", problems);

        // ---- parsing the shipped GLSL --------------------------------------------------------------------

        readonly record struct Declaration(int Set, int Binding, GpuResourceKind Kind, string Name);

        // Memory and precision qualifiers, which GLSL allows on EITHER side of the storage keyword. Declared
        // first because the pattern below is built from this list rather than repeating it, and a static field
        // initialiser reads the ones above it.
        static readonly string[] MemoryQualifiers =
            ["readonly", "writeonly", "coherent", "volatile", "restrict", "highp", "mediump", "lowp"];

        // One layout qualifier list, then any memory qualifiers, then the storage keyword, then whatever it
        // declares up to the ; or {. The qualifier list is matched as a whole because it carries other keys in
        // shipped code (std430 first on every storage buffer, an rgba16f format qualifier on the ocean's storage
        // image), so a pattern anchored on "layout(set" alone would miss exactly the compute declarations this
        // most needs to see.
        //
        // THE MEMORY QUALIFIER GROUP IS THERE BECAUSE GLSL TAKES THEM ON EITHER SIDE of the storage keyword and
        // the canonical placement is BEFORE it: `layout(...) readonly buffer B { ... }`. A pattern that demanded
        // uniform|buffer immediately after the layout group skipped that spelling entirely. The skip is not
        // silent (the coverage test then reports the layout element as declared by no stage) but it is the wrong
        // problem reported loudly, which costs whoever reads it the time to find out the parser never saw the
        // line. Nothing shipped is spelled that way today, which is exactly why this has to be right now rather
        // than on the day a kernel is.
        static readonly Regex DeclarationPattern = new(
            @"layout\s*\((?<keys>[^)]*)\)\s*(?<memory>(?:(?:" + string.Join("|", MemoryQualifiers)
            + @")\s+)*)(?<storage>uniform|buffer)\s+(?<rest>[^;{]+)[;{]",
            RegexOptions.Compiled);

        static readonly Regex KeyPattern = new(@"\b(set|binding)\s*=\s*(\d+)", RegexOptions.Compiled);

        static IEnumerable<Declaration> Parse(string glsl)
        {
            foreach (Match match in DeclarationPattern.Matches(glsl))
            {
                int set = -1, binding = -1;
                foreach (Match key in KeyPattern.Matches(match.Groups["keys"].Value))
                {
                    int value = int.Parse(key.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (key.Groups[1].Value == "set") set = value;
                    else binding = value;
                }

                // A layout qualifier with no set and no binding is a vertex input location or a workgroup size,
                // which this test has nothing to say about.
                if (set < 0 || binding < 0) continue;

                string[] declared = match.Groups["rest"].Value
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                string[] words = declared
                    .Where(w => !MemoryQualifiers.Contains(w, StringComparer.Ordinal))
                    .ToArray();

                // Read from BOTH sides of the storage keyword, since either is legal and neither is preferred by
                // anything but habit.
                bool readOnly = match.Groups["memory"].Value
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Concat(declared)
                        .Contains("readonly", StringComparer.Ordinal);

                yield return new Declaration(set, binding, KindOf(match.Groups["storage"].Value, words, readOnly),
                    words.Length > 0 ? words[^1] : "?");
            }
        }

        static GpuResourceKind KindOf(string storage, string[] words, bool readOnly)
        {
            string type = words.Length > 0 ? words[0] : "";

            // A storage buffer block, and the ONE place the readonly qualifier changes the answer. The engine
            // declares no buffer readonly today, and the qualifier is read rather than assumed away because a
            // readonly block is the other engine kind and both are legal. It deliberately does NOT do the same
            // for a storage image: a readonly image2D is still a storage image to Vulkan, the same descriptor
            // type as a writable one, so mapping it to the sampled-texture kind would be a new bug rather than a
            // fixed one.
            if (string.Equals(storage, "buffer", StringComparison.Ordinal))
            {
                return readOnly
                    ? GpuResourceKind.StructuredBufferReadOnly
                    : GpuResourceKind.StructuredBufferReadWrite;
            }

            if (type.StartsWith("image", StringComparison.Ordinal)) return GpuResourceKind.TextureReadWrite;
            if (type.StartsWith("texture", StringComparison.Ordinal)) return GpuResourceKind.TextureReadOnly;
            if (string.Equals(type, "sampler", StringComparison.Ordinal)) return GpuResourceKind.Sampler;

            // Anything else after `uniform` is a block name, and a block is a uniform buffer. The shared GLSL
            // declares texture2D and sampler SEPARATELY and never a combined sampler2D, which is the convention
            // the descriptor mapping already relies on (8.1), so there is no combined case to model.
            return GpuResourceKind.UniformBuffer;
        }
    }
}
