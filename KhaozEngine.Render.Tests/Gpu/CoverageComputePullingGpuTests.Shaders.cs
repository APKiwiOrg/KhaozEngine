using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputePullingGpuTests
{
    [Theory]
    [InlineData("CoverageModel")]
    [InlineData("CoverageModelDissolve")]
    [InlineData("CoverageModelMotion")]
    [InlineData("CoverageModelDissolveMotion")]
    [InlineData("CoverageOutlineFull")]
    [InlineData("CoverageOutlineVisible")]
    public void Pulled_programs_have_no_IA_inputs_and_precise_gap_free_interpolants(string name)
    {
        var program = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == name);
        var artifacts = new Dictionary<string, byte[]>();
        var rows = new List<ShaderCorpusRow>();
        ShaderCorpus.EmitGraphics(program, rows, (key, data) => artifacts.Add(key, data));
        string Text(string key) => Encoding.UTF8.GetString(artifacts[name + key]);
        Assert.DoesNotContain(" in vec", program.VertexGlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("sink", program.VertexGlsl, StringComparison.Ordinal);
        string vertex = Text(".vertex.hlsl"), fragment = Text(".fragment.hlsl");
        Assert.Contains("precise", vertex, StringComparison.Ordinal);
        Assert.Empty(Semantics(vertex));
        uint[] inputs = Semantics(fragment);
        Assert.NotEmpty(inputs);
        Assert.Equal(Enumerable.Range(0, inputs.Length).Select(i => (uint)i), inputs);
        if (KhaozEngineD3D11.IsPlatformSupported)
            _ = D3D11ShaderBuild.Pair(program.VertexGlsl, program.FragmentGlsl,
                D3D11ShaderDebug.Optimized, cache: null, options: new(PreciseMath: true));
        string path = EvidencePath;
        Directory.CreateDirectory(path);
        foreach (var artifact in artifacts) File.WriteAllBytes(Path.Combine(path, artifact.Key), artifact.Value);
        File.WriteAllLines(Path.Combine(path, name + ".corpus"), rows.Select(r =>
            $"{r.Key} {r.Hash} {r.Size.ToString(CultureInfo.InvariantCulture)}" + (r.Detail.Length == 0 ? "" : " " + r.Detail)));
    }

    static uint[] Semantics(string hlsl)
    {
        var block = Regex.Match(hlsl, @"struct SPIRV_Cross_Input\s*\{(?<fields>[\s\S]*?)\};");
        return Regex.Matches(block.Groups["fields"].Value, @"TEXCOORD(\d+)")
            .Select(m => uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
    }
}
