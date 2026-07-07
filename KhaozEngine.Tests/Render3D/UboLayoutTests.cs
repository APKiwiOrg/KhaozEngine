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
        public void UboBytes_EqualsHeaderPlusTwoLightArrays()
        {
            // The renderer derives UboBytes = HeaderBytes + 2 * LightArrayBytes and uploads the two point-light
            // arrays at HeaderBytes and HeaderBytes + LightArrayBytes (see ModelRenderer.WriteFrameUniformsTo).
            // Assert that composition as an equation of the named constants; if it drifts, the splat params tail
            // and the light-array uploads land at the wrong offsets.
            Assert.Equal(
                ModelRenderer.HeaderBytes + 2 * ModelRenderer.LightArrayBytes,
                ModelRenderer.UboBytes);
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
        public void UboBytes_Is688_TheDocumentedCombinedSize()
        {
            // 176 + 2*256 = 688. The value the comments/docs quote; a sanity anchor on the derived arithmetic.
            Assert.Equal(688u, ModelRenderer.UboBytes);
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
        public void SplatTail_AppendsAtOffset688_PerGlslComment()
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
            // GLSL: Final { vec4 BgColor; vec4 Params; } = 2 vec4 = 32 bytes (BlitFrag).
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
