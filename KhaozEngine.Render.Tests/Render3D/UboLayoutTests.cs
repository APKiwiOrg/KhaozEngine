using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Tripwire tests for the hand-computed C# &lt;-&gt; GLSL std140 UBO layout agreements. The 3D renderer mirrors
    /// GLSL uniform blocks (in <see cref="ShaderSources"/>) with C# structs and hand-computed byte offsets
    /// (<see cref="ModelRenderer"/> HeaderBytes/UboBytes, <see cref="SplatParamsData"/> SizeInBytes,
    /// <see cref="PixelPostProcess"/> buffer sizes). Those agreements were enforced only by "MUST mirror" comments;
    /// these tests turn them into red/green checks so a drift on EITHER half (struct or shader) fails headlessly
    /// under plain <c>dotnet test</c>. Every failure message names the drifted pair and where the other half lives.
    /// </summary>
    public class UboLayoutTests
    {
        // ---- Model pass: FrameUbo header + point-light tail = the combined UBO size ----

        [Fact]
        public void FrameUbo_MarshalSize_EqualsHeaderBytesConstant()
        {
            Assert.Equal(
                (int)ModelRenderer.HeaderBytes,
                Marshal.SizeOf<ModelRenderer.FrameUbo>());
        }

        [Fact]
        public void FrameUbo_HeaderBytes_Is176_PerGlslBlock()
        {
            // 1 mat4 (64) + 7 vec4 (7*16 = 112) = 176. If this ever changes, the GLSL `U` block in ShaderSources
            // (ModelVert/ModelFrag) must change in lockstep or the per-frame upload smears the light arrays.
            Assert.Equal(64 + 7 * 16, (int)ModelRenderer.HeaderBytes);
        }

        [Fact]
        public void UboBytes_EqualsHeaderPlusLightArraysPlusShadowTail()
        {
            // The renderer derives UboBytes = HeaderBytes + 2 * LightArrayBytes + ShadowTailBytes and uploads the two
            // point-light arrays then the shadow tail (see ModelRenderer.WriteFrameUniformsTo). Assert that
            // composition as an equation of the named constants; if it drifts, the splat params tail, the light-array
            // uploads, or the shadow-tail upload land at the wrong offsets.
            Assert.Equal(
                ModelRenderer.HeaderBytes + 2 * ModelRenderer.LightArrayBytes + ModelRenderer.ShadowTailBytes,
                ModelRenderer.UboBytes);
        }

        [Fact]
        public void ShadowTail_MarshalSize_And_Offset_MatchConstants()
        {
            // The cascaded shadow tail (mat4 ShadowMat[MaxCascades] + vec4 Params + vec4 Params2 + vec4 NormalOffsets)
            // is 304 bytes, appended right after the two light arrays at offset 688. If the ShadowUbo struct or the
            // constants drift apart the shadow-tail upload (WriteFrameUniformsTo -> UpdateBuffer at ShadowTailOffset)
            // smears the splat params that follow.
            Assert.Equal((int)ModelRenderer.ShadowTailBytes, Marshal.SizeOf<ModelRenderer.ShadowUbo>());
            Assert.Equal(ModelRenderer.MaxCascades * 64 + 3 * 16, (int)ModelRenderer.ShadowTailBytes);
            Assert.Equal(ModelRenderer.HeaderBytes + 2 * ModelRenderer.LightArrayBytes, ModelRenderer.ShadowTailOffset);
            Assert.Equal(688u, ModelRenderer.ShadowTailOffset);
        }

        [Fact]
        public void LightArrayBytes_EqualsMaxPointLightsTimesVec4Stride()
        {
            // std140 array stride for a vec4 is 16 bytes. LightArrayBytes = MaxPointLights * 16.
            Assert.Equal(
                (uint)(ModelRenderer.MaxPointLights * 16),
                ModelRenderer.LightArrayBytes);
        }

        [Fact]
        public void UboBytes_Is992_TheDocumentedCombinedSize()
        {
            // 176 + 2*256 + 304 = 992. The value the comments/docs quote, a sanity anchor on the derived arithmetic
            // (header + both point-light arrays + the cascaded shadow tail = mat4[4] + 3*vec4).
            Assert.Equal(992u, ModelRenderer.UboBytes);
        }

        // ---- Model instance stream (InstanceData) + the dynamic-geometry decal tag (issue #235) ----

        [Fact]
        public void InstanceData_MarshalSize_EqualsDeclaredSizeInBytes_124()
        {
            // The per-instance vertex stream is Model (mat4, 64) + Tint + Emissive + SpecParams (3*16) + IsDynamic
            // (float, 4) + Dissolve (Vector2, 8) = 124 bytes. ModelRenderer's instance vertex layout uses
            // InstanceData.SizeInBytes as the stride and adds an IDynamic Float1 (location 12) then an IDissolve
            // Float2 (location 13) for the two trailing fields. If the struct and the constant drift, the instanced
            // draws fetch each instance at the wrong offset (garbled transforms/tints).
            Assert.Equal(124, (int)ModelRenderer.InstanceData.SizeInBytes);
            Assert.Equal((int)ModelRenderer.InstanceData.SizeInBytes, Marshal.SizeOf<ModelRenderer.InstanceData>());
        }

        [Fact]
        public void InstanceData_FieldOffsets_KeepExistingElementsFixed_AndAppendDissolve()
        {
            // The dissolve field (issue #253) is appended AFTER IsDynamic so every pre-existing instance element
            // keeps its byte offset: a scene that queues no dissolve fetches Model/Tint/Emissive/SpecParams/IsDynamic
            // at the exact same offsets as before, which is what keeps the GPU goldens byte-identical. Assert each
            // offset so a reorder (which would silently move location 12/13 in the vertex layout) trips here.
            Assert.Equal(0, (int)Marshal.OffsetOf<ModelRenderer.InstanceData>(nameof(ModelRenderer.InstanceData.Model)));
            Assert.Equal(64, (int)Marshal.OffsetOf<ModelRenderer.InstanceData>(nameof(ModelRenderer.InstanceData.Tint)));
            Assert.Equal(80, (int)Marshal.OffsetOf<ModelRenderer.InstanceData>(nameof(ModelRenderer.InstanceData.Emissive)));
            Assert.Equal(96, (int)Marshal.OffsetOf<ModelRenderer.InstanceData>(nameof(ModelRenderer.InstanceData.SpecParams)));
            Assert.Equal(112, (int)Marshal.OffsetOf<ModelRenderer.InstanceData>(nameof(ModelRenderer.InstanceData.IsDynamic)));
            Assert.Equal(116, (int)Marshal.OffsetOf<ModelRenderer.InstanceData>(nameof(ModelRenderer.InstanceData.Dissolve)));
        }

        [Fact]
        public void ModelShaders_CarryTheDynamicGeometryTag()
        {
            // The dynamic-geometry decal mask rides InstanceData.IsDynamic -> IDynamic (location 12) -> vDynamic ->
            // oNormal.a on the rigid/CPU-skinned path, and P[3].x -> vDynamic on the GPU-skinned path. Assert the GLSL
            // halves so a struct/layout change without the shader (or vice versa) trips headlessly. The static world
            // (splat terrain) keeps alpha 1, which is what makes a no-skinned scene byte-identical.
            Assert.Contains("layout(location=12) in float IDynamic;", ShaderSources.ModelVert);
            Assert.Contains("1.0 - clamp(vDynamic, 0.0, 1.0)", ShaderSources.ModelFrag);
            Assert.Contains("1.0 - clamp(vDynamic, 0.0, 1.0)", ShaderSources.ModelDissolveFrag);
            Assert.Contains("1.0 - clamp(vDynamic, 0.0, 1.0)", ShaderSources.SkinnedModelFrag);
            Assert.Contains("1.0 - clamp(vDynamic, 0.0, 1.0)", ShaderSources.SkinnedModelDissolveFrag);
            Assert.Contains("vDynamic = P[3].x;", ShaderSources.SkinnedModelVert);
            Assert.Contains("oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0);", ShaderSources.SplatFrag);
        }

        [Fact]
        public void ModelShaders_CarryThePerInstanceDissolve()
        {
            // The rigid/instanced dissolve (issue #253) rides InstanceData.Dissolve -> IDissolve (location 13) ->
            // vDissolve (location 10) and is folded INTO ModelFrag gated by an if (dissolve <= 0 keeps the old path
            // byte-exact), NOT reusing ModelDissolveFrag. Assert the GLSL halves so a struct/layout change without
            // the shader (or vice versa) trips headlessly, and that the gate is a branch and not a multiply.
            Assert.Contains("layout(location=13) in vec2 IDissolve;", ShaderSources.ModelVert);
            Assert.Contains("vDissolve = IDissolve;", ShaderSources.ModelVert);
            Assert.Contains("layout(location=10) in vec2 vDissolve;", ShaderSources.ModelFrag);
            Assert.Contains("if (vDissolve.x > 0.0)", ShaderSources.ModelFrag);
        }

        // ---- Splat material params tail ----

        [Fact]
        public void SplatParamsData_MarshalSize_EqualsDeclaredSizeInBytes()
        {
            Assert.Equal(
                (int)SplatParamsData.SizeInBytes,
                Marshal.SizeOf<SplatParamsData>());
        }

        [Fact]
        public void SplatParamsData_SizeInBytes_MatchesGlslTail_7Vec4()
        {
            // The SplatParams tail in the SplatFrag `U` block is TintTiling[5] + Roughness + Misc = 5 + 1 + 1 = 7
            // vec4 = 112 bytes. The 5 comes from SplatMaterialConfig.LayerCount.
            int expected = (SplatMaterialConfig.LayerCount + 2) * 16;
            Assert.Equal((int)SplatParamsData.SizeInBytes, expected);
        }

        [Fact]
        public void SplatTail_AppendsAtUboBytesOffset_PerGlslComment()
        {
            // SplatVert/SplatFrag document "per-material params appended (offset 688)" and the renderer writes the
            // params tail at UboBytes (ModelRenderer.CreateSplatParamsUbo -> UpdateBuffer at UboBytes). Assert the
            // C# append offset equals the value quoted in the SplatVert GLSL comment; if UboBytes drifts, that
            // comment (the other half, in ShaderSources.SplatVert) and the append offset disagree.
            string glslOffset = "(offset " + ModelRenderer.UboBytes + ")";
            Assert.True(ShaderSources.SplatVert.Contains(glslOffset),
                $"SplatVert no longer documents '{glslOffset}': the splat params append offset drifted from ModelRenderer.UboBytes ({ModelRenderer.UboBytes}). Fix the ShaderSources.SplatVert comment or the UBO size.");
        }

        // ---- Post-process UBOs (PixelPostProcess) ----

        [Fact]
        public void EdgeUbo_MarshalSize_EqualsEdgeBufferAllocation()
        {
            // GLSL: Edge { vec4 OutlineColor; vec4 Texel; vec4 Thresh; vec4 Fade; } = 4 vec4 = 64 bytes (EdgeFrag).
            Assert.Equal(
                (int)PixelPostProcess.EdgeBufferBytes,
                Marshal.SizeOf<PixelPostProcess.EdgeUbo>());
        }

        [Fact]
        public void FinalUbo_MarshalSize_EqualsFinalBufferAllocation()
        {
            // GLSL: Final { vec4 Params; } = 1 vec4 = 16 bytes (BlitFrag).
            Assert.Equal(
                (int)PixelPostProcess.FinalBufferBytes,
                Marshal.SizeOf<PixelPostProcess.FinalUbo>());
        }

        [Fact]
        public void PaletteScratch_MatchesPaletteBuffer_And_GlslColorsArray()
        {
            // The Pal block is `vec4 Colors[MaxPaletteColors]; vec4 Info;`. The CPU scratch mirrors it flat as
            // floats, so PaletteScratchFloats * 4 must equal the GPU buffer byte size (PaletteBufferBytes). If they
            // drift, the palette upload over/under-fills the GPU buffer (PixelPostProcess.PrepareUniforms).
            Assert.Equal(
                PixelPostProcess.PaletteBufferBytes,
                (uint)PixelPostProcess.PaletteScratchFloats * sizeof(float));
            // And the scratch is exactly (MaxPaletteColors + 1) vec4 worth of floats.
            Assert.Equal(
                (PixelPostProcess.MaxPaletteColors + 1) * 4,
                PixelPostProcess.PaletteScratchFloats);
        }

        [Fact]
        public void FxaaBuffer_IsOneVec4()
        {
            // GLSL: Fxaa { vec4 Rcp; } = 1 vec4 = 16 bytes (FxaaFrag).
            Assert.Equal(16u, PixelPostProcess.FxaaBufferBytes);
        }

        // ---- Bloom UBOs (PixelPostProcess) ----

        [Fact]
        public void BrightUbo_MarshalSize_EqualsBrightBufferAllocation()
        {
            // GLSL: Bright { vec4 Params; } = 1 vec4 = 16 bytes (BloomBrightFrag).
            Assert.Equal(
                (int)PixelPostProcess.BrightBufferBytes,
                Marshal.SizeOf<PixelPostProcess.BrightUbo>());
        }

        [Fact]
        public void CompositeUbo_MarshalSize_EqualsCompositeBufferAllocation()
        {
            // GLSL: Composite { vec4 Params; } = 1 vec4 = 16 bytes (BloomCompositeFrag).
            Assert.Equal(
                (int)PixelPostProcess.CompositeBufferBytes,
                Marshal.SizeOf<PixelPostProcess.CompositeUbo>());
        }

        [Fact]
        public void ToneUbo_MarshalSize_EqualsToneBufferAllocation()
        {
            // GLSL: Tone { vec4 Params; } = 1 vec4 = 16 bytes (TonemapFrag).
            Assert.Equal(16u, PixelPostProcess.ToneBufferBytes);
            Assert.Equal(
                (int)PixelPostProcess.ToneBufferBytes,
                Marshal.SizeOf<PixelPostProcess.ToneUbo>());
        }

        [Fact]
        public void ApplyUbo_MarshalSize_EqualsApplyBufferAllocation()
        {
            // GLSL: Apply { vec4 Params; } = 1 vec4 = 16 bytes (DistortionApplyFrag).
            Assert.Equal(16u, PixelPostProcess.ApplyBufferBytes);
            Assert.Equal(
                (int)PixelPostProcess.ApplyBufferBytes,
                Marshal.SizeOf<PixelPostProcess.ApplyUbo>());
        }

        [Fact]
        public void BlurScratch_MatchesBlurBuffer_And_GlslWeightsArray()
        {
            // The Blur block is `vec4 Texel; vec4 Params; vec4 Weights[BlurWeightSlots];`. The CPU scratch mirrors
            // it flat as floats, so BlurScratchFloats * 4 must equal the GPU buffer byte size (BlurBufferBytes). If
            // they drift, the per-axis blur upload over/under-fills the GPU buffer (PixelPostProcess.PrepareUniforms).
            Assert.Equal(
                PixelPostProcess.BlurBufferBytes,
                (uint)PixelPostProcess.BlurScratchFloats * sizeof(float));
            // Texel (1 vec4) + Params (1 vec4) + Weights (BlurWeightSlots vec4) = BlurScratchFloats/4.
            Assert.Equal(
                4 + 4 + PixelPostProcess.BlurWeightSlots * 4,
                PixelPostProcess.BlurScratchFloats);
            // BlurWeightSlots must cover BloomMath.MaxRadius + 1 taps (index 0..MaxRadius).
            Assert.Equal(BloomMath.MaxRadius + 1, PixelPostProcess.BlurWeightSlots);
        }

        [Fact]
        public void BloomBlurFrag_WeightsArray_SizedByBlurWeightSlots()
        {
            // BloomBlurFrag declares Weights[9] (BlurWeightSlots). Build the GLSL spelling from the C# constant so
            // the array size and PixelPostProcess.BlurWeightSlots stay coupled.
            string weightsArray = "vec4 Weights[" + PixelPostProcess.BlurWeightSlots + "];";
            Assert.True(ShaderSources.BloomBlurFrag.Contains(weightsArray),
                $"BloomBlurFrag lost '{weightsArray}': the blur weight slot count drifted from PixelPostProcess.BlurWeightSlots ({PixelPostProcess.BlurWeightSlots}). Fix ShaderSources.BloomBlurFrag or the constant.");
        }

        // ---- GLSL <-> C# constant agreement (string assertions against the embedded shader sources) ----

        [Fact]
        public void ModelShaders_DeclarePointLightArrays_SizedByMaxPointLights()
        {
            // The two point-light arrays in the ModelVert/ModelFrag `U` block are sized [MaxPointLights]. Build the
            // exact GLSL spelling FROM the C# constant and assert both stages contain it, so bumping MaxPointLights
            // without editing the shader (or vice versa) trips. The other half lives in ShaderSources.ModelVert/Frag.
            string posArray = "vec4 PointPosRadius[" + ModelRenderer.MaxPointLights + "];";
            string colArray = "vec4 PointColorIntensity[" + ModelRenderer.MaxPointLights + "];";

            Assert.True(ShaderSources.ModelVert.Contains(posArray),
                $"ModelVert lost '{posArray}': the point-light array size drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.ModelVert or the constant.");
            Assert.True(ShaderSources.ModelVert.Contains(colArray),
                $"ModelVert lost '{colArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.ModelVert or the constant.");
            Assert.True(ShaderSources.ModelFrag.Contains(posArray),
                $"ModelFrag lost '{posArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.ModelFrag or the constant.");
            Assert.True(ShaderSources.ModelFrag.Contains(colArray),
                $"ModelFrag lost '{colArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.ModelFrag or the constant.");
        }

        [Fact]
        public void SplatShaders_DeclarePointLightArrays_SizedByMaxPointLights()
        {
            // SplatVert/SplatFrag share the same frame UBO header (incl. the two point-light arrays) as the model
            // pass. Same tripwire, other half in ShaderSources.SplatVert/Frag.
            string posArray = "vec4 PointPosRadius[" + ModelRenderer.MaxPointLights + "];";
            string colArray = "vec4 PointColorIntensity[" + ModelRenderer.MaxPointLights + "];";

            Assert.True(ShaderSources.SplatVert.Contains(posArray),
                $"SplatVert lost '{posArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.SplatVert or the constant.");
            Assert.True(ShaderSources.SplatVert.Contains(colArray),
                $"SplatVert lost '{colArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.SplatVert or the constant.");
            Assert.True(ShaderSources.SplatFrag.Contains(posArray),
                $"SplatFrag lost '{posArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.SplatFrag or the constant.");
            Assert.True(ShaderSources.SplatFrag.Contains(colArray),
                $"SplatFrag lost '{colArray}': drifted from ModelRenderer.MaxPointLights ({ModelRenderer.MaxPointLights}). Fix ShaderSources.SplatFrag or the constant.");
        }

        [Fact]
        public void SplatShaders_DeclareTintTilingArray_SizedByLayerCount()
        {
            // The per-material params tail begins with TintTiling[LayerCount]. Build the GLSL spelling from the C#
            // SplatMaterialConfig.LayerCount so bumping the layer count without editing the shader trips. The other
            // half lives in ShaderSources.SplatVert/Frag (the `U` block tail).
            string tintTiling = "vec4 TintTiling[" + SplatMaterialConfig.LayerCount + "];";

            Assert.True(ShaderSources.SplatVert.Contains(tintTiling),
                $"SplatVert lost '{tintTiling}': the splat layer count drifted from SplatMaterialConfig.LayerCount ({SplatMaterialConfig.LayerCount}). Fix ShaderSources.SplatVert or the constant.");
            Assert.True(ShaderSources.SplatFrag.Contains(tintTiling),
                $"SplatFrag lost '{tintTiling}': drifted from SplatMaterialConfig.LayerCount ({SplatMaterialConfig.LayerCount}). Fix ShaderSources.SplatFrag or the constant.");
        }

        [Fact]
        public void SplatFrag_PerLayerLoopAndArrays_SizedByLayerCount()
        {
            // The fragment reconstructs per-layer weight/roughness arrays and loops over the layers with the literal
            // count. Assert the loop bound and the fixed-size float arrays are spelled with LayerCount, so the shader
            // body stays in sync with SplatMaterialConfig.LayerCount too (not just the UBO array).
            int n = SplatMaterialConfig.LayerCount;
            string loop = "for (int L = 0; L < " + n + "; L++)";
            string wArray = "float w[" + n + "] = float[" + n + "]";
            string rghArray = "float rgh[" + n + "] = float[" + n + "]";

            Assert.True(ShaderSources.SplatFrag.Contains(loop),
                $"SplatFrag lost '{loop}': the per-layer loop bound drifted from SplatMaterialConfig.LayerCount ({n}). Fix ShaderSources.SplatFrag or the constant.");
            Assert.True(ShaderSources.SplatFrag.Contains(wArray),
                $"SplatFrag lost '{wArray}': the weight array size drifted from SplatMaterialConfig.LayerCount ({n}). Fix ShaderSources.SplatFrag or the constant.");
            Assert.True(ShaderSources.SplatFrag.Contains(rghArray),
                $"SplatFrag lost '{rghArray}': the roughness array size drifted from SplatMaterialConfig.LayerCount ({n}). Fix ShaderSources.SplatFrag or the constant.");
        }

        [Fact]
        public void AllFrameUboShaders_DeclareCascadedShadowTail()
        {
            // The cascaded shadow tail (mat4 ShadowMat[4] + vec4 ShadowParams + vec4 ShadowParams2 + vec4
            // ShadowNormalOffsets) rides in the frame UBO after the light arrays, in ALL FOUR shaders that declare the
            // `U` block (both stages of model + splat). If any one drops or mis-sizes a member, the block layout
            // diverges and the shadow tail / splat params tail land at the wrong offset on that stage.
            string mats = "mat4 ShadowMat[" + ModelRenderer.MaxCascades + "];";
            foreach (var (name, src) in new[]
            {
                ("ModelVert", ShaderSources.ModelVert), ("ModelFrag", ShaderSources.ModelFrag),
                ("SplatVert", ShaderSources.SplatVert), ("SplatFrag", ShaderSources.SplatFrag),
            })
            {
                Assert.True(src.Contains(mats),
                    $"{name} lost '{mats}': the cascaded shadow matrices dropped or mis-sized in the frame UBO block; the splat params tail now lands at the wrong offset. Fix ShaderSources.{name} or ModelRenderer.MaxCascades.");
                Assert.True(src.Contains("vec4 ShadowParams;"),
                    $"{name} lost 'vec4 ShadowParams;': the shadow tail dropped from the frame UBO block. Fix ShaderSources.{name}.");
                Assert.True(src.Contains("vec4 ShadowParams2;"),
                    $"{name} lost 'vec4 ShadowParams2;': the shadow tail dropped from the frame UBO block. Fix ShaderSources.{name}.");
                Assert.True(src.Contains("vec4 ShadowNormalOffsets;"),
                    $"{name} lost 'vec4 ShadowNormalOffsets;': the per-cascade normal-offset vec4 dropped from the frame UBO block; the splat params tail now lands at the wrong offset. Fix ShaderSources.{name}.");
            }
        }

        [Fact]
        public void ModelAndSplatFrag_SampleTheShadowMap_ViaSharedHelper()
        {
            // Both fragments must call the single-sourced PCF helper so model + terrain shadow IDENTICALLY. If either
            // stops calling it, that surface silently stops receiving shadows (the lockstep invariant breaks).
            Assert.Contains("sampleKeyShadow(", ShaderSources.LightingCommonGlsl);
            Assert.Contains("sampleKeyShadow(ShadowMap, ShadowSamp", ShaderSources.ModelFrag);
            Assert.Contains("sampleKeyShadow(ShadowMap, ShadowSamp", ShaderSources.SplatFrag);
        }

        // ---- Sky background pass UBO (SkyRenderer) ----

        [Fact]
        public void SkyUbo_MarshalSize_EqualsUboBytesConstant_And_GlslBlock()
        {
            // GLSL Sky block: 6 vec4 (Horizon, Zenith, SunColor, SunNdc, Params, Res) = 96 bytes. If the struct or
            // the shader block drift apart, the per-frame sky UBO upload smears the colours / sun params. The other
            // half lives in ShaderSources.SkyFrag.
            Assert.Equal((int)SkyRenderer.UboBytes, Marshal.SizeOf<SkyRenderer.SkyUbo>());
            Assert.Equal(6 * 16, (int)SkyRenderer.UboBytes);
        }

        [Fact]
        public void SkyFrag_DeclaresTheSixVec4Members()
        {
            // Assert every member of the Sky block is present (in the fragment that reads it), so a rename/reorder on
            // the shader side that silently changes the layout trips here. Other half: ShaderSources.SkyFrag.
            foreach (var member in new[] { "vec4 Horizon;", "vec4 Zenith;", "vec4 SunColor;",
                "vec4 SunNdc;", "vec4 Params;", "vec4 Res;" })
                Assert.True(ShaderSources.SkyFrag.Contains(member),
                    $"SkyFrag lost '{member}': the Sky UBO block drifted from SkyRenderer.SkyUbo. Fix ShaderSources.SkyFrag or the struct.");
        }

        // ---- Water surface pass UBO (WaterRenderer) ----

        [Fact]
        public void WaterUbo_MarshalSize_EqualsPayloadBytesConstant_And_GlslBlock()
        {
            // GLSL Water block: 2 mat4 (ViewProj, InvViewProj) + 19 vec4 (LightDir, LightColor, CameraPos,
            // DeepColor, ShallowColor, HorizonColor, WaveParams, ShoreGlint, DetailParams, SkyHorizon, SkyZenith,
            // SkySunColor, SkyParams, ReflectGlint, SwellParams, SwellShape, Absorption, FoamColor, FoamParams)
            // = 128 + 304 = 432 bytes. If the struct or the shader block drift apart, the per-plane water UBO
            // upload smears the colours/wave/swell/foam params. Other half: ShaderSources.WaterFrag.
            Assert.Equal((int)WaterRenderer.PayloadBytes, Marshal.SizeOf<WaterRenderer.WaterUbo>());
            Assert.Equal(2 * 64 + 19 * 16, (int)WaterRenderer.PayloadBytes);
        }

        [Fact]
        public void WaterUbo_BoundRangeAndStride_SatisfyTheD3D11ConstantCountRule()
        {
            // D3D11's PSSetConstantBuffers1 requires FirstConstant AND NumConstants to be multiples of 16 constants,
            // and Veldrid 4.9.0 derives them as offset/16 and max(size, 256)/16 with NO rounding. So a bound size
            // under 256 is padded to 256 and is fine, an exact multiple of 256 is fine, and ANYTHING IN BETWEEN
            // yields a non-multiple-of-16 count that D3D11 rejects outright - leaving the whole cbuffer unbound, so
            // the shader reads zeros. That is not hypothetical: 14.22.0 grew this payload to 272 and binding 272
            // made every water fragment read opacity 0 and discard, so D3D11/WARP rendered NO WATER while Metal and
            // Vulkan were perfect. Only the cross-backend bake caught it.
            //
            // This asserts the fix as an invariant on the two numbers rather than on the backend: the bound range
            // (SlotBytes, not PayloadBytes) is 256-aligned, and it covers the payload so a plane's params are whole.
            Assert.True(WaterRenderer.SlotBytes % 256 == 0,
                $"water UBO bound range {WaterRenderer.SlotBytes} is not a multiple of 256: D3D11 will reject the " +
                "constant-buffer binding and the water pass will silently render nothing on that backend.");
            Assert.True(WaterRenderer.SlotBytes >= WaterRenderer.PayloadBytes,
                $"water UBO slot stride {WaterRenderer.SlotBytes} is smaller than its {WaterRenderer.PayloadBytes}-byte payload: adjacent planes would overwrite each other.");
        }

        [Fact]
        public void WaterFrag_DeclaresAllMembers()
        {
            // Assert every member of the Water block is present (in the fragment that reads it), so a
            // rename/reorder on the shader side that silently changes the layout trips here. Other half:
            // ShaderSources.WaterFrag.
            foreach (var member in new[] { "mat4 ViewProj;", "mat4 InvViewProj;", "vec4 LightDir;", "vec4 LightColor;",
                "vec4 CameraPos;", "vec4 DeepColor;", "vec4 ShallowColor;", "vec4 HorizonColor;", "vec4 WaveParams;",
                "vec4 ShoreGlint;", "vec4 DetailParams;", "vec4 SkyHorizon;", "vec4 SkyZenith;", "vec4 SkySunColor;",
                "vec4 SkyParams;", "vec4 ReflectGlint;", "vec4 SwellParams;", "vec4 SwellShape;", "vec4 Absorption;",
                "vec4 FoamColor;", "vec4 FoamParams;" })
                Assert.True(ShaderSources.WaterFrag.Contains(member),
                    $"WaterFrag lost '{member}': the Water UBO block drifted from WaterRenderer.WaterUbo. Fix ShaderSources.WaterFrag or the struct.");
        }

        [Fact]
        public void WaterVert_DeclaresSameBlockAsWaterFrag()
        {
            // The vertex stage only READS ViewProj, but the block must be declared identically in both stages
            // (same one-UBO-per-set buffer) or the two stages disagree on the layout the driver builds.
            foreach (var member in new[] { "mat4 ViewProj;", "mat4 InvViewProj;", "vec4 LightDir;", "vec4 LightColor;",
                "vec4 CameraPos;", "vec4 DeepColor;", "vec4 ShallowColor;", "vec4 HorizonColor;", "vec4 WaveParams;",
                "vec4 ShoreGlint;", "vec4 DetailParams;", "vec4 SkyHorizon;", "vec4 SkyZenith;", "vec4 SkySunColor;",
                "vec4 SkyParams;", "vec4 ReflectGlint;", "vec4 SwellParams;", "vec4 SwellShape;", "vec4 Absorption;",
                "vec4 FoamColor;", "vec4 FoamParams;" })
                Assert.True(ShaderSources.WaterVert.Contains(member),
                    $"WaterVert lost '{member}': the Water UBO block declaration drifted from WaterFrag's. Fix ShaderSources.WaterVert.");
        }

        // ---- Distortion offset-field pass UBO (DistortionRenderer) ----

        [Fact]
        public void DistortionFrameUbo_MarshalSize_EqualsFrameBufferBytes_And_GlslBlock()
        {
            // GLSL Frame block: 2 mat4 (ViewProj, InvViewProj) + 4 vec4 (CamRight, CamUp, CamPosTime, Params) =
            // 128 + 64 = 192 bytes. If the struct or the shader block drift apart, the per-frame distortion UBO
            // upload smears the camera basis / params. The other half lives in ShaderSources.DistortionVert/Frag.
            Assert.Equal((int)DistortionRenderer.FrameBufferBytes, Marshal.SizeOf<DistortionRenderer.FrameUniforms>());
            Assert.Equal(2 * 64 + 4 * 16, (int)DistortionRenderer.FrameBufferBytes);
        }

        [Fact]
        public void DistortionShaders_DeclareTheFrameBlockMembers_InBothStages()
        {
            // Assert every member of the Frame block is present in BOTH stages (one UBO per set, declared identically
            // in vertex and fragment or the driver builds two conflicting layouts). Other half: ShaderSources.Distortion*.
            foreach (var member in new[] { "mat4 ViewProj;", "mat4 InvViewProj;", "vec4 CamRight;",
                "vec4 CamUp;", "vec4 CamPosTime;", "vec4 Params;" })
            {
                Assert.True(ShaderSources.DistortionVert.Contains(member),
                    $"DistortionVert lost '{member}': the Frame UBO block drifted from DistortionRenderer.FrameUniforms.");
                Assert.True(ShaderSources.DistortionFrag.Contains(member),
                    $"DistortionFrag lost '{member}': the Frame UBO block drifted from DistortionVert's declaration.");
            }
        }

        // ---- Starfield background pass UBO (StarfieldRenderer) ----

        [Fact]
        public void StarfieldUbo_matches_the_gpu_allocation()
        {
            // 2 * vec4 = 32. Every member is 16-byte aligned, so std140 needs no extra padding.
            Assert.Equal((int)StarfieldRenderer.UboBytes, Marshal.SizeOf<StarfieldRenderer.StarfieldUbo>());
            Assert.Equal(32, (int)StarfieldRenderer.UboBytes);
        }

        [Fact]
        public void PaletteFrag_ColorsArray_SizedByMaxPaletteColors()
        {
            // The palette-quantize block declares Colors[MaxPaletteColors]. Build the GLSL spelling from the C#
            // constant so the palette buffer sizing and the shader array stay coupled. Other half: ShaderSources.PaletteFrag.
            string colorsArray = "vec4 Colors[" + PixelPostProcess.MaxPaletteColors + "];";
            Assert.True(ShaderSources.PaletteFrag.Contains(colorsArray),
                $"PaletteFrag lost '{colorsArray}': the palette size drifted from PixelPostProcess.MaxPaletteColors ({PixelPostProcess.MaxPaletteColors}). Fix ShaderSources.PaletteFrag or the constant.");
        }
    }
}
