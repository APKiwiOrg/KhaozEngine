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
                ["Sprite2DMasked"] = "SpriteBatch masked",
                ["GpuScaledCopy"] = "GpuScaledCopy",

                ["Model"] = "ModelRenderer",
                ["Foliage"] = "ModelRenderer foliage",
                ["PreparedFoliage"] = "ModelRenderer prepared foliage",
                ["PreparedFoliageOpaque"] = "ModelRenderer prepared foliage opaque",
                ["PreparedFoliageOpaqueMotion"] = "ModelRenderer prepared foliage opaque motion",
                ["PreparedFoliageMotion"] = "ModelRenderer prepared foliage motion",
                ["PreparedFoliageDepth"] = "FoliageShadowRenderer prepared depth",
                ["PreparedFoliageDepthCutout"] = "FoliageShadowRenderer prepared cutout depth",
                ["PreparedFoliagePoint"] = "PointShadowRenderer prepared foliage",
                ["FoliageOpaque"] = "ModelRenderer foliage opaque",
                ["ModelDissolve"] = "ModelRenderer dissolve",
                ["SkinnedModel"] = "ModelRenderer skinned",
                ["SkinnedModelDissolve"] = "ModelRenderer skinned dissolve",
                ["Splat"] = "ModelRenderer splat",
                ["TileGround"] = "ModelRenderer tile ground",
                ["TileGroundRelief"] = "ModelRenderer tile ground relief",
                ["TileGroundReliefHex"] = "ModelRenderer tile ground relief hex",
                ["ModelMotion"] = "ModelRenderer motion",
                ["SkinnedModelMotion"] = "ModelRenderer skinned motion",
                ["SkinnedModelDissolveMotion"] = "ModelRenderer skinned motion",
                ["ModelCpuSkinnedMotion"] = "ModelRenderer cpu skinned motion",
                ["ModelCpuSkinnedDissolveMotion"] = "ModelRenderer cpu skinned motion",
                ["FoliageMotion"] = "ModelRenderer foliage motion",
                ["FoliageOpaqueMotion"] = "ModelRenderer foliage opaque motion",
                ["SplatMotion"] = "ModelRenderer splat motion",
                ["TileGroundMotion"] = "ModelRenderer tile ground motion",
                ["TileGroundReliefMotion"] = "ModelRenderer tile ground relief motion",
                ["TileGroundReliefHexMotion"] = "ModelRenderer tile ground relief hex motion",
                ["TexturedBillboardMotion"] = "TexturedBillboardRenderer",
                ["BeamMotion"] = "BeamRenderer",
                ["TrailMotion"] = "TrailRenderer",
                ["OverlayMeshMotion"] = "OverlayMeshRenderer",
                ["SilhouetteMotion"] = "SilhouetteRenderer",

                // Visibility adds fragment outputs and preserves each program's resource layout.
                ["ModelVisibility"] = "ModelRenderer",
                ["FoliageVisibility"] = "ModelRenderer foliage",
                ["PreparedFoliageVisibility"] = "ModelRenderer prepared foliage",
                ["PreparedFoliageOpaqueVisibility"] = "ModelRenderer prepared foliage opaque",
                ["PreparedFoliageOpaqueMotionVisibility"] = "ModelRenderer prepared foliage opaque motion",
                ["PreparedFoliageMotionVisibility"] = "ModelRenderer prepared foliage motion",
                ["FoliageOpaqueVisibility"] = "ModelRenderer foliage opaque",
                ["ModelDissolveVisibility"] = "ModelRenderer dissolve",
                ["SkinnedModelVisibility"] = "ModelRenderer skinned",
                ["SkinnedModelDissolveVisibility"] = "ModelRenderer skinned dissolve",
                ["SplatVisibility"] = "ModelRenderer splat",
                ["TileGroundVisibility"] = "ModelRenderer tile ground",
                ["TileGroundReliefVisibility"] = "ModelRenderer tile ground relief",
                ["TileGroundReliefHexVisibility"] = "ModelRenderer tile ground relief hex",
                ["ModelMotionVisibility"] = "ModelRenderer motion",
                ["SkinnedModelMotionVisibility"] = "ModelRenderer skinned motion",
                ["SkinnedModelDissolveMotionVisibility"] = "ModelRenderer skinned motion",
                ["ModelCpuSkinnedMotionVisibility"] = "ModelRenderer cpu skinned motion",
                ["ModelCpuSkinnedDissolveMotionVisibility"] = "ModelRenderer cpu skinned motion",
                ["FoliageMotionVisibility"] = "ModelRenderer foliage motion",
                ["FoliageOpaqueMotionVisibility"] = "ModelRenderer foliage opaque motion",
                ["SplatMotionVisibility"] = "ModelRenderer splat motion",
                ["TileGroundMotionVisibility"] = "ModelRenderer tile ground motion",
                ["TileGroundReliefMotionVisibility"] = "ModelRenderer tile ground relief motion",
                ["TileGroundReliefHexMotionVisibility"] = "ModelRenderer tile ground relief hex motion",
                ["TexturedBillboardVisibility"] = "TexturedBillboardRenderer",
                ["TexturedBillboardMotionVisibility"] = "TexturedBillboardRenderer",
                ["BeamVisibility"] = "BeamRenderer",
                ["BeamMotionVisibility"] = "BeamRenderer",
                ["TrailVisibility"] = "TrailRenderer",
                ["TrailMotionVisibility"] = "TrailRenderer",
                ["OverlayMeshVisibility"] = "OverlayMeshRenderer",
                ["OverlayMeshMotionVisibility"] = "OverlayMeshRenderer",
                ["SilhouetteVisibility"] = "SilhouetteRenderer",
                ["SilhouetteMotionVisibility"] = "SilhouetteRenderer",

                // Rigid dissolve adds the cut set, while opaque and excluded skinned depth keep their layouts.
                ["FoliageDepth"] = "FoliageShadowRenderer depth",
                ["FoliageDepthCutout"] = "FoliageShadowRenderer cutout depth",
                ["ShadowDepth"] = "ShadowMapRenderer depth",
                ["ShadowDepthDissolve"] = "ShadowMapRenderer dissolve depth",
                ["ShadowDepthDissolveInverted"] = "ShadowMapRenderer dissolve depth",
                ["SkinnedShadowDepth"] = "ShadowMapRenderer skinned depth",
                ["SkinnedShadowDepthDissolve"] = "ShadowMapRenderer skinned depth",
                ["SkinnedShadowDepthDissolveInverted"] = "ShadowMapRenderer skinned depth",
                // Rigid cutout keeps albedo at set 1 and appends the interior cut at set 2.
                ["ShadowDepthCutout"] = "ShadowMapRenderer cutout depth",
                ["ShadowDepthCutoutInverted"] = "ShadowMapRenderer cutout depth",

                // Point-light rigid dissolve also appends the cut set. Opaque, clear and skinned stay uncut.
                ["PointShadowRigid"] = "PointShadowRenderer",
                ["PointShadowRigidDissolve"] = "PointShadowRenderer dissolve",
                ["PointShadowRigidDissolveInverted"] = "PointShadowRenderer dissolve",
                ["PointShadowSkinned"] = "PointShadowRenderer skinned",
                ["PointShadowSkinnedDissolve"] = "PointShadowRenderer skinned dissolve",
                ["PointShadowSkinnedDissolveInverted"] = "PointShadowRenderer skinned dissolve inverted",
                ["PointShadowClear"] = "PointShadowRenderer",

                ["Beam"] = "BeamRenderer",
                // The Line pair has three call sites and two of them go through OverlayRenderer, whose layout is
                // the same single uniform block. Its primary one is named here, as in the catalog.
                ["Line"] = "DepthLineRenderer",
                ["Billboard"] = "BillboardRenderer",
                ["TexturedBillboard"] = "TexturedBillboardRenderer",
                ["Particle"] = "ParticleRenderer",
                ["Trail"] = "TrailRenderer",
                ["Distortion"] = "DistortionRenderer",
                ["GroundDecal"] = "GroundDecalRenderer",
                ["OverlayMesh"] = "OverlayMeshRenderer",

                ["TargetOutlineRigidFull"] = "TargetOutlineRenderer rigid full",
                ["TargetOutlineRigidVisible"] = "TargetOutlineRenderer rigid visible",
                ["TargetOutlineComposite"] = "TargetOutlineRenderer composite",
                ["TargetOutlineSkinnedFull"] = "TargetOutlineRenderer skinned full",
                ["TargetOutlineSkinnedVisible"] = "TargetOutlineRenderer skinned visible",

                ["Sky"] = "SkyRenderer",
                ["AtmosphereTransmittance"] = "AtmosphereRenderer transmittance",
                ["AtmosphereMultiScattering"] = "AtmosphereRenderer multi-scattering",
                ["AtmosphereSkyView"] = "AtmosphereRenderer sky-view",
                ["Starfield"] = "StarfieldRenderer",
                ["Water"] = "WaterRenderer",
                ["WaterClipmap"] = "WaterRenderer",
                ["BoundedWater"] = "WaterRenderer bounded",
                ["WaterTriangle"] = "WaterRenderer",
                ["WaterClipmapTriangle"] = "WaterRenderer",
                ["WaterSurfaceField"] = "WaterCausticRenderer field",
                ["WaterCausticSubtract"] = "WaterCausticRenderer subtract",
                ["WaterCausticSheet"] = "WaterCausticRenderer sheet",

                ["PostPalette"] = "PixelPostProcess pal",
                ["PostEdge"] = "PixelPostProcess edge",
                ["PostTemporalEdge"] = "PixelPostProcess temporal edge",
                ["PostBlit"] = "PixelPostProcess blit",
                ["PostFxaa"] = "PixelPostProcess fxaa",
                ["PostTonemap"] = "PixelPostProcess tone",
                ["PostDistortionApply"] = "PixelPostProcess apply",
                ["PostTemporalSharpen"] = "TemporalSharpenPass",
                ["PostBloomBright"] = "PixelPostProcess bright",
                ["PostBloomBlur"] = "PixelPostProcess blur",
                ["PostBloomComposite"] = "PixelPostProcess composite",

                ["TransitionSolid"] = "TransitionRenderer solid",
                ["TransitionCrossfade"] = "TransitionRenderer cross",
                ["MotionVectorsView"] = "MotionVectorsView",

                ["TemporalResolve"] = "TemporalResolveRenderer resolve",
                ["TemporalDepthStore"] = "TemporalResolveRenderer depth store",
                ["TemporalDebugView"] = "TemporalResolveRenderer debug view",
                ["TemporalCountProbe"] = "TemporalResolveRenderer count probe",
                ["TemporalPrepare"] = "TemporalSplitResolve prepare",
                ["TemporalAccumulate"] = "TemporalSplitResolve accumulate",
                // The half-precision programs bind exactly as their full-precision twins.
                ["TemporalResolveHalf"] = "TemporalResolveRenderer resolve",
                ["TemporalAccumulateHalf"] = "TemporalSplitResolve accumulate",
                ["TemporalDebugViewHalf"] = "TemporalResolveRenderer debug view",
                ["TemporalCountProbeHalf"] = "TemporalResolveRenderer count probe",
                // The second pass at the display's own size binds exactly as the upscaling one.
                ["TemporalAccumulateAtDisplaySize"] = "TemporalSplitResolve accumulate",
                ["TemporalAccumulateAtDisplaySizeHalf"] = "TemporalSplitResolve accumulate",
                // Certified programs omit the visibility resources from their compact layouts.
                ["TemporalResolveFullyLit"] = "TemporalResolveRenderer fully lit resolve",
                ["TemporalResolveHalfFullyLit"] = "TemporalResolveRenderer fully lit resolve",
                ["TemporalDebugViewFullyLit"] = "TemporalResolveRenderer fully lit debug view",
                ["TemporalDebugViewHalfFullyLit"] = "TemporalResolveRenderer fully lit debug view",
                ["TemporalCountProbeFullyLit"] = "TemporalResolveRenderer fully lit count probe",
                ["TemporalCountProbeHalfFullyLit"] = "TemporalResolveRenderer fully lit count probe",
                ["TemporalAccumulateFullyLit"] = "TemporalSplitResolve fully lit accumulate",
                ["TemporalAccumulateHalfFullyLit"] = "TemporalSplitResolve fully lit accumulate",
                ["TemporalAccumulateAtDisplaySizeFullyLit"] = "TemporalSplitResolve fully lit accumulate",
                ["TemporalAccumulateAtDisplaySizeHalfFullyLit"] = "TemporalSplitResolve fully lit accumulate",
            };

        static readonly IReadOnlyDictionary<string, ShippedGraphicsProgram> GraphicsByName =
            ShippedShaderPrograms.GraphicsPrograms().ToDictionary(p => p.Name, StringComparer.Ordinal);

        static readonly IReadOnlyDictionary<string, ShippedComputeKernel> ComputeByName =
            ShippedShaderPrograms.ComputeKernels().ToDictionary(k => k.Name, StringComparer.Ordinal);

        /// <summary>
        /// EVERY DECLARED <c>(set, binding)</c> IS A REAL INDEX PAIR, and the kind at it matches. This is the
        /// assertion the decision asks for: N indexes the pipeline's layout array and M indexes that layout's
        /// element array.
        /// </summary>
        [Theory]
        [MemberData(nameof(GraphicsPrograms))]
        public void EveryShippedGraphicsProgram_DeclaresSetAndBindingAtTheArrayIndices(string name)
        {
            ShippedGraphicsProgram program = GraphicsByName[name];
            var problems = new List<string>();
            string[] slots = SlotsOf(program.Name, problems);
            Assert.NotEmpty(slots);
            Declaration[] declarations = Parse(program.VertexGlsl).Concat(Parse(program.FragmentGlsl)).ToArray();
            Assert.NotEmpty(declarations);
            foreach (Declaration d in declarations) Check(program.Name, slots, d, problems);
            Assert.True(problems.Count == 0, Report(problems));
        }

        [Theory]
        [MemberData(nameof(OpaquePrograms))]
        public void OpaqueReceivers_AppendAtmosphereAfterTheirOwnSetZeroPrefix(string name, int prefix)
        {
            ShippedGraphicsProgram program = GraphicsByName[name];
            Declaration[] atmosphere =
            [
                new(0, prefix, GpuResourceKind.UniformBuffer, "Atmosphere"),
                new(0, prefix + 1, GpuResourceKind.TextureReadOnly, "AtmoSkyView"),
                new(0, prefix + 2, GpuResourceKind.Sampler, "AtmoSkySampler"),
            ];
            if (prefix == 11)
                atmosphere = [.. atmosphere, new(0, 14, GpuResourceKind.TextureReadOnly, "InteriorCut")];
            int waterBinding = prefix == 11 ? 15 : prefix + 3;
            atmosphere = [.. atmosphere,
                new(0, waterBinding, GpuResourceKind.StructuredBufferReadOnly, "WaterVolumeBuffer"),
                new(0, waterBinding + 1, GpuResourceKind.TextureReadOnly, "CausticMap"),
                new(0, waterBinding + 2, GpuResourceKind.Sampler, "CausticSampler")];
            // The rigid model layout ends with the material tail (#1776), after the water bindings.
            if (prefix == 11)
                atmosphere = [.. atmosphere,
                    new(0, 18, GpuResourceKind.Sampler, "NormalSamp"),
                    new(0, 19, GpuResourceKind.Sampler, "RoughnessSamp"),
                    new(0, 20, GpuResourceKind.TextureReadOnly, "EmissiveMap"),
                    new(0, 21, GpuResourceKind.Sampler, "EmissiveSamp"),
                    new(0, 22, GpuResourceKind.UniformBuffer, "MaterialParams")];
            Assert.Equal(atmosphere, Parse(program.FragmentGlsl)
                .Where(d => d.Set == 0 && d.Binding >= prefix).OrderBy(d => d.Binding).ToArray());
        }

        [Theory]
        [InlineData("Water")]
        [InlineData("WaterClipmap")]
        [InlineData("BoundedWater")]
        [InlineData("WaterTriangle")]
        [InlineData("WaterClipmapTriangle")]
        public void WaterAndClipmap_AppendAtmosphereAfterTheDynamicWaterUniform(string name)
        {
            var elements = VulkanDescriptorLimitTests.ShippedLayouts["Water"].Elements!;
            Assert.Equal(17, elements.Length);
            Assert.Equal("Water", elements[6].Name);
            Assert.True(elements[6].Dynamic);
            Declaration[] atmosphere =
            [
                new(0, 7, GpuResourceKind.UniformBuffer, "Atmosphere"),
                new(0, 8, GpuResourceKind.TextureReadOnly, "AtmoSkyView"),
                new(0, 9, GpuResourceKind.Sampler, "AtmoSkySampler"),
                new(0, 10, GpuResourceKind.TextureReadOnly, "OpticsTex"),
                new(0, 11, GpuResourceKind.Sampler, "OpticsSamp"),
                new(0, 12, GpuResourceKind.TextureReadOnly, "ColumnShadowAtlas"),
                new(0, 13, GpuResourceKind.Sampler, "ColumnShadowSampler"),
                new(0, 14, GpuResourceKind.UniformBuffer, "WaterLitFrame"),
                new(0, 15, GpuResourceKind.TextureReadOnly, "ColumnSceneColor"),
                new(0, 16, GpuResourceKind.Sampler, "ColumnColorSampler"),
            ];
            ShippedGraphicsProgram program = GraphicsByName[name];
            Assert.Equal(atmosphere, Parse(program.FragmentGlsl).Where(d => d.Set == 0 && d.Binding >= 7)
                .OrderBy(d => d.Binding).ToArray());
        }

        /// <summary>The compute half, over all four cascade resolutions of both ocean kernels.</summary>
        [Theory]
        [MemberData(nameof(ComputePrograms))]
        public void EveryShippedComputeKernel_DeclaresSetAndBindingAtTheArrayIndices(string name)
        {
            ShippedComputeKernel kernel = ComputeByName[name];
            var problems = new List<string>();
            string[] slots = SlotsOf(PipelineOf(kernel.Name), problems, kernel.Name);
            Assert.NotEmpty(slots);
            Declaration[] declarations = Parse(kernel.ComputeGlsl).ToArray();
            Assert.NotEmpty(declarations);
            foreach (Declaration d in declarations) Check(kernel.Name, slots, d, problems);
            Assert.True(problems.Count == 0, Report(problems));
        }

        /// <summary>
        /// AND THE DECLARATIONS COVER THE LAYOUTS COMPLETELY, which is the half the per-declaration check above
        /// cannot see. A layout element nothing declares is a descriptor the engine writes and no stage reads,
        /// which is either a dead binding or, far worse, an array that has drifted by one and happens to still be
        /// in range at every index a shader names.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllPrograms))]
        public void EveryLayoutElementOfEveryShippedPipeline_IsDeclaredBySomeStageOfItsProgram(string name)
        {
            var (slots, declarations) = LayoutProgram(name);
            Assert.NotEmpty(slots);
            var declared = declarations.Select(d => (d.Set, d.Binding)).ToHashSet();
            var problems = new List<string>();
            for (int set = 0; set < slots.Length; set++)
            {
                GpuResourceLayoutElement[] elements =
                    VulkanDescriptorLimitTests.ShippedLayouts[slots[set]].Elements ?? [];
                for (int binding = 0; binding < elements.Length; binding++)
                {
                    if (declared.Contains((set, binding))) continue;
                    problems.Add($"{name}: layout slot {set} ({slots[set]}) element {binding} "
                        + $"({elements[binding].Name}, {elements[binding].Kind}) is declared by no stage of "
                        + "the program. Either the pipeline's layout array carries a resource nothing reads, "
                        + "or it has drifted out of step with the shader sources.");
                }
            }
            Assert.True(problems.Count == 0, Report(problems));
        }

        /// <summary>
        /// The independent rosters must match every discovered program name. A missing catalog item must fail
        /// even though it generates no theory case. Every mapped pipeline must also exist in the layout table.
        /// </summary>
        [Fact]
        public void EveryShippedProgram_HasAPipelineAndEveryPipelineNamedIsReal()
        {
            string[] catalog = GraphicsByName.Keys.ToArray();
            var pipelines = VulkanDescriptorLimitTests.ShippedPipelines
                .Select(p => p.Pipeline)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(ProgramPipelines.Keys.OrderBy(n => n, StringComparer.Ordinal),
                catalog.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(ComputeRoster.OrderBy(n => n, StringComparer.Ordinal),
                ComputeByName.Keys.OrderBy(n => n, StringComparer.Ordinal));
            foreach (string pipeline in ProgramPipelines.Values.Concat(["OceanFftProducer row",
                         "OceanFftProducer col"]))
            {
                Assert.True(pipelines.Contains(pipeline),
                    $"This map names the pipeline {pipeline}, which VulkanDescriptorLimitTests.ShippedPipelines "
                    + "does not have. A renamed pipeline has to be renamed here too.");
            }
        }

        static readonly string[] ComputeRoster =
        [
            "OceanFftRowPass32", "OceanFftColumnPass32", "OceanFftRowPass64", "OceanFftColumnPass64",
            "OceanFftRowPass128", "OceanFftColumnPass128", "OceanFftRowPass256", "OceanFftColumnPass256",
        ];

        public static TheoryData<string> GraphicsPrograms => Names(
            GraphicsByName.Keys);

        public static TheoryData<string> ComputePrograms => Names(
            ComputeByName.Keys);

        public static TheoryData<string> AllPrograms => Names(
            GraphicsByName.Keys
                .Concat(ComputeByName.Keys));

        public static TheoryData<string, int> OpaquePrograms
        {
            get
            {
                var cases = new TheoryData<string, int>();
                foreach (ShippedGraphicsProgram program in GraphicsByName.Values)
                {
                    string name = program.Name.EndsWith("Visibility", StringComparison.Ordinal)
                        ? program.Name[..^"Visibility".Length] : program.Name;
                    // Other programs do not export the opaque receiver's atmosphere layout.
                    int prefix = name switch
                    {
                        "Model" or "ModelDissolve" or "Foliage" or "ModelMotion" or "ModelCpuSkinnedMotion"
                            or "ModelCpuSkinnedDissolveMotion" or "FoliageMotion"
                            or "PreparedFoliage" or "PreparedFoliageMotion" => 11,
                        "FoliageOpaque" or "FoliageOpaqueMotion" or "PreparedFoliageOpaque" or "PreparedFoliageOpaqueMotion" => 7,
                        "SkinnedModel" or "SkinnedModelDissolve" or "SkinnedModelMotion"
                            or "SkinnedModelDissolveMotion" => 4,
                        "Splat" or "TileGround" or "SplatMotion" or "TileGroundMotion"
                            or "TileGroundRelief" or "TileGroundReliefMotion"
                            or "TileGroundReliefHex" or "TileGroundReliefHexMotion" => 3,
                        _ => -1,
                    };
                    if (prefix >= 0) cases.Add(program.Name, prefix);
                }
                return cases;
            }
        }

        static TheoryData<string> Names(IEnumerable<string> names)
        {
            var cases = new TheoryData<string>();
            foreach (string name in names) cases.Add(name);
            return cases;
        }

        // ---- the join ------------------------------------------------------------------------------------

        static (string[] Slots, IEnumerable<Declaration> Declarations) LayoutProgram(string name)
        {
            if (GraphicsByName.TryGetValue(name, out ShippedGraphicsProgram program))
                return (SlotsOf(name, null), Parse(program.VertexGlsl).Concat(Parse(program.FragmentGlsl)));
            ShippedComputeKernel kernel = ComputeByName[name];
            return (SlotsOf(PipelineOf(name), null), Parse(kernel.ComputeGlsl));
        }

        // The ocean kernels are compiled per cascade resolution, so the four names of each pass share one
        // pipeline shape. The separate compute roster checks every admitted resolution independently of this join.
        static string PipelineOf(string kernelName)
            => kernelName.StartsWith("OceanFftRowPass", StringComparison.Ordinal)
                ? "OceanFftProducer row"
                : "OceanFftProducer col";

        internal static string[] SlotsOf(string program, List<string>? problems, string? reportAs = null)
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
