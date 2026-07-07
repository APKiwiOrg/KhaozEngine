using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Tripwires for the single-sourced GLSL lighting block. The key+fill+cel+point-light lighting math lives in ONE
    /// place (<see cref="ShaderSources.LightingCommonGlsl"/>) and is concatenated into both <see cref="ShaderSources.ModelFrag"/>
    /// and <see cref="ShaderSources.SplatFrag"/> at compile time, then invoked by each. These tests guard the
    /// composition so a future edit cannot silently re-inline a private copy (the divergence the whole refactor
    /// removed) or drop the shared call, and so the intentional terrain-specific spec-exponent divergence stays named.
    /// </summary>
    public sealed class LightingCompositionTests
    {
        const string SharedFunctionSignature =
            "void computeLighting(vec3 N, vec3 worldPos, float specStrength, float specExp, out vec3 diffuse, out vec3 specColor)";
        const string SharedFunctionCall = "computeLighting(N, vWorldPos, specStrength, specExp, diffuse, specColor);";

        static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        [Fact]
        public void ModelFrag_ContainsSharedLightingDefinition_ExactlyOnce()
        {
            // Composition sanity: the shared block is spliced in verbatim, so the definition appears once. More than
            // one means someone re-inlined a copy (the hand-duplication this refactor exists to prevent); zero means
            // the splice was dropped. Other half: ShaderSources.LightingCommonGlsl / ModelFrag composition.
            Assert.Equal(1, CountOccurrences(ShaderSources.ModelFrag, SharedFunctionSignature));
        }

        [Fact]
        public void SplatFrag_ContainsSharedLightingDefinition_ExactlyOnce()
        {
            Assert.Equal(1, CountOccurrences(ShaderSources.SplatFrag, SharedFunctionSignature));
        }

        [Fact]
        public void ModelFrag_InvokesSharedLighting()
        {
            // The definition being present is not enough; the fragment must actually call it (a dropped call would
            // leave diffuse/specColor undefined and lighting off).
            Assert.Contains(SharedFunctionCall, ShaderSources.ModelFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void SplatFrag_InvokesSharedLighting()
        {
            Assert.Contains(SharedFunctionCall, ShaderSources.SplatFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void SplatFrag_NamesTheTerrainSpecExponentConstants()
        {
            // The terrain-roughness-derived spec exponent is the ONE intentional divergence from ModelFrag (blended
            // layers carry no per-instance material). It is spelled with named consts and a mix over roughness, not a
            // bare literal, so the divergence stays documented at the call site. Other half: ShaderSources.SplatFrag.
            Assert.Contains("const float SPLAT_SPEC_EXP_SMOOTH = 48.0;", ShaderSources.SplatFrag, StringComparison.Ordinal);
            Assert.Contains("const float SPLAT_SPEC_EXP_ROUGH  = 8.0;", ShaderSources.SplatFrag, StringComparison.Ordinal);
            Assert.Contains("mix(SPLAT_SPEC_EXP_SMOOTH, SPLAT_SPEC_EXP_ROUGH, rough)", ShaderSources.SplatFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void ModelFrag_DerivesSpecExponentFromPerInstanceParams()
        {
            // ModelFrag keeps deriving the exponent from its per-instance shininess (vSpecParams.y); this is the
            // other side of the intentional divergence and must not drift into the terrain constants.
            Assert.Contains("mix(vSpecParams.y, 8.0, rough)", ShaderSources.ModelFrag, StringComparison.Ordinal);
        }
    }
}
