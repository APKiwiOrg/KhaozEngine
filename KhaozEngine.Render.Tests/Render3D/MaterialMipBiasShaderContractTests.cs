using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// EVERY MATERIAL PROGRAM TAKES THE TEMPORAL MIP BIAS (TEMPORAL-RESOLVE-UPSCALING-DESIGN risk 4). A material
    /// program is a shipped program whose fragment reads the shared frame block and declares a material map
    /// (<c>Albedo</c> or <c>AlbedoArray</c>). Discovered from <see cref="ShippedShaderPrograms"/> rather than listed,
    /// so a new material program, or a motion variant of one, is held to the rule the moment it ships.
    /// </summary>
    public sealed class MaterialMipBiasShaderContractTests
    {
        const string FrameBlockMarker = "vec4 PointPosRadius[16];";
        static readonly Regex UnbiasedMapTap =
            new(@"texture\(\s*sampler2D\(\s*(Albedo|NormalMap|RoughnessMap)\s*,", RegexOptions.CultureInvariant);
        static readonly Regex Hoist = new(@"vec3\s+(dWx|dWy)\s*=\s*([^;]+);", RegexOptions.CultureInvariant);

        static bool IsMaterialProgram(ShippedGraphicsProgram p) =>
            p.FragmentGlsl.Contains(FrameBlockMarker, StringComparison.Ordinal)
            && (p.FragmentGlsl.Contains("uniform texture2D Albedo;", StringComparison.Ordinal)
                || p.FragmentGlsl.Contains("uniform texture2DArray AlbedoArray;", StringComparison.Ordinal));

        static List<string> Problems(ShippedGraphicsProgram p)
        {
            var problems = new List<string>();
            string frag = p.FragmentGlsl;
            bool mapTaps = frag.Contains("uniform texture2D Albedo;", StringComparison.Ordinal);
            bool gradTaps = frag.Contains("textureGrad(", StringComparison.Ordinal);
            if (mapTaps)
            {
                if (!frag.Contains(ShaderSources.MaterialLodSampleGlsl, StringComparison.Ordinal))
                    problems.Add($"{p.Name}: samples material maps without splicing MaterialLodSampleGlsl");
                if (UnbiasedMapTap.IsMatch(frag))
                    problems.Add($"{p.Name}: samples a material map with texture() instead of materialSample()");
                if (!frag.Contains("materialSample(Albedo, Samp, vUv)", StringComparison.Ordinal))
                    problems.Add($"{p.Name}: never samples Albedo through materialSample()");
            }
            if (gradTaps)
            {
                if (!frag.Contains(ShaderSources.MaterialLodGradGlsl, StringComparison.Ordinal))
                    problems.Add($"{p.Name}: takes textureGrad taps without splicing MaterialLodGradGlsl");
                MatchCollection hoists = Hoist.Matches(frag);
                if (hoists.Count != 2)
                    problems.Add($"{p.Name}: expected the hoisted derivatives dWx and dWy, found {hoists.Count}");
                foreach (Match m in hoists)
                    if (!m.Groups[2].Value.Contains("* materialGradScale()", StringComparison.Ordinal))
                        problems.Add($"{p.Name}: {m.Groups[1].Value} is not scaled by materialGradScale()");
            }
            if (!mapTaps && !gradTaps)
                problems.Add($"{p.Name}: declares a material map and samples it by neither path");
            return problems;
        }

        [Fact]
        public void TheDiscoveryFindsEveryMaterialFamily()
        {
            string[] names = ShippedShaderPrograms.GraphicsPrograms().Where(IsMaterialProgram)
                .Select(p => p.Name).ToArray();
            string[] families =
                { "Model", "Foliage", "ModelDissolve", "SkinnedModel", "SkinnedModelDissolve", "Splat", "TileGround" };
            foreach (string family in families)
                Assert.Contains(family, names);
        }

        [Fact]
        public void EveryMaterialProgramTakesTheBias()
        {
            List<string> problems = ShippedShaderPrograms.GraphicsPrograms().Where(IsMaterialProgram)
                .SelectMany(Problems).ToList();
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void TheCheckRejectsAProgramThatSamplesAroundTheBias()
        {
            var unbiased = new ShippedGraphicsProgram("Unbiased", "", FrameBlockMarker
                + "\nuniform texture2D Albedo;\nvoid main() { vec4 c = texture(sampler2D(Albedo, Samp), vUv); }");
            Assert.True(IsMaterialProgram(unbiased));
            Assert.NotEmpty(Problems(unbiased));
        }

        [Fact]
        public void TheBiasReadsTheFrameBlocksFreeParamsLanes()
        {
            Assert.Contains("texture(sampler2D(map, samp), uv, Params.z)", ShaderSources.MaterialLodSampleGlsl,
                StringComparison.Ordinal);
            Assert.Contains("1.0 + Params.w", ShaderSources.MaterialLodGradGlsl, StringComparison.Ordinal);
        }
    }
}
