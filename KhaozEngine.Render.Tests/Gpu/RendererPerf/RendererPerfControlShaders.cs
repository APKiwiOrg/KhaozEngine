using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfControlShaderArtifact(string Name, string SourceSha256, string SpirvSha256,
    string HlslSha256, string MslSha256, uint LocalX, int FmaInstructions, int Divides, int Roots, int Stores);

// Cold compiler evidence. These are intermediate emissions, not physical GPU instruction or occupancy counters.
internal static class RendererPerfControlShaders
{
    static readonly Lazy<ImmutableArray<RendererPerfControlShaderArtifact>> Artifacts = new(Compile);
    internal static ImmutableArray<RendererPerfControlShaderArtifact> Corpus => Artifacts.Value;
    internal static string Source(string name)
    {
        using Stream stream = typeof(RendererPerfControlShaders).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Gpu.RendererPerf.controls." + name + ".glsl")
            ?? throw new InvalidOperationException("Missing embedded control shader " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8, false);
        return reader.ReadToEnd();
    }
    internal static string ComputeName(int id) => id switch
    {
        4 or 5 or 6 => "chain.comp",
        7 or 8 => "stream.comp",
        9 => "fma.comp",
        10 => "dependent.comp",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
    static ImmutableArray<RendererPerfControlShaderArtifact> Compile()
    {
        var result = ImmutableArray.CreateBuilder<RendererPerfControlShaderArtifact>();
        foreach (string name in new[] { "chain.comp", "stream.comp", "fma.comp", "dependent.comp" })
        {
            string source = Source(name);
            ShaderValidation.ValidateCompute(source, name);
            byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, name);
            var hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, name);
            var msl = SpirvCrossCompile.ComputeToMsl(spirv, name);
            var local = SpirvLocalSize.Parse(spirv, name);
            int fma = Count(spirv, 12, 50), roots = Count(spirv, 12, 31), divides = Count(spirv, 136), stores = Count(spirv, 62);
            if (stores == 0 || local != (name == "stream.comp" ? (256u, 1u, 1u) : name == "chain.comp" ? (1u, 1u, 1u) : (64u, 1u, 1u))
                || name == "fma.comp" && fma != 128 || name == "dependent.comp" && (roots != 64 || divides != 64))
                throw new InvalidOperationException(name + " lost its declared emitted work or local size.");
            if (KhaozEngineD3D11.IsPlatformSupported) KhaozEngineD3D11.ValidateComputeShader(source, name);
            result.Add(new(name, Hash(source), RendererPerfRecipe.Hash(spirv), Hash(hlsl.ComputeSource),
                Hash(msl.ComputeSource), local.X, fma, divides, roots, stores));
        }
        result.Add(CompilePair("fullscreen", "fullscreen.frag", 4));
        result.Add(CompilePair("depth-read", "depth-read.frag", 1));
        return result.ToImmutable();
    }
    static RendererPerfControlShaderArtifact CompilePair(string name, string fragmentName, int stores)
    {
        string vertex = Source("fullscreen.vert"), fragment = Source(fragmentName);
        ShaderValidation.ValidatePair(vertex, fragment, name);
        byte[] vert = SpirvFrontEnd.ToSpirv(vertex, GpuShaderStages.Vertex, name);
        byte[] frag = SpirvFrontEnd.ToSpirv(fragment, GpuShaderStages.Fragment, name);
        var hp = SpirvCrossCompile.VertexFragmentToHlsl(vert, frag, name);
        var mp = SpirvCrossCompile.VertexFragmentToMsl(vert, frag, name);
        if (Count(frag, 62) != stores || hp.Reflection.VertexElements.Length != 0 || mp.Reflection.VertexElements.Length != 0)
            throw new InvalidOperationException(name + " lost its MRT outputs or gained vertex inputs.");
        if (KhaozEngineD3D11.IsPlatformSupported) KhaozEngineD3D11.ValidateShaderPair(vertex, fragment, name);
        return new(name, Hash(vertex + fragment), RendererPerfRecipe.Hash([.. vert, .. frag]),
            Hash(hp.VertexSource + hp.FragmentSource), Hash(mp.VertexSource + mp.FragmentSource), 0, 0, 0, 0, stores);
    }
    internal static int Count(byte[] spirv, uint opcode, uint? extension = null)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
        int count = 0;
        for (int i = 5; i < words.Length;)
        {
            int length = (int)(words[i] >> 16);
            if (length == 0 || i + length > words.Length) throw new InvalidOperationException("Malformed control SPIR-V.");
            if ((words[i] & 65535) == opcode && (extension is null || length > 4 && words[i + 4] == extension)) count++;
            i += length;
        }
        return count;
    }
    internal static string Hash(string text) => RendererPerfRecipe.Hash(Encoding.UTF8.GetBytes(text));
    internal static string Digest => RendererPerfRecipe.Digest(Corpus);
}
