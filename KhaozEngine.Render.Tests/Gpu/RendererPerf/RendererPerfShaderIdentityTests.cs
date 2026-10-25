using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfShaderIdentityTests
{
    [Theory]
    [InlineData(nameof(ShaderSources.ModelMotionVert))]
    [InlineData(nameof(ShaderSources.SkinnedModelMotionVert))]
    [InlineData(nameof(ShaderSources.ModelCpuSkinnedMotionVert))]
    [InlineData(nameof(ShaderSources.ModelMotionFrag))]
    [InlineData(nameof(ShaderSources.SkinnedModelMotionFrag))]
    [InlineData(nameof(ShaderSources.TemporalEdgeFrag))]
    [InlineData(nameof(ShaderSources.WaterVert))]
    [InlineData(nameof(ShaderSources.WaterClipmapVert))]
    public void Generated_source_only_change_changes_shader_digest(string field)
    {
        string name = typeof(ShaderSources).FullName + "." + field;
        string source = (string)typeof(ShaderSources).GetField(field)!.GetValue(null)!;
        var sources = (SortedDictionary<string, string>)typeof(RendererPerfExecutable)
            .GetMethod("ShaderSourcesByName", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Assert.True(sources.ContainsKey(name), "Generated GPU source is absent: " + name);
        Assert.Equal(source, sources[name]);
        Assert.Equal(Hash(source), RendererPerfExecutable.ShaderHashes[name]);
        Assert.Equal(RendererPerfExecutable.ShaderSha256, Digest(sources));
        sources[name] = source + "\n// generated-source-only revision\n";
        Assert.NotEqual(RendererPerfExecutable.ShaderSha256, Digest(sources));
        Assert.Equal(Digest(sources), Digest(sources.Reverse()));
    }

    [Theory]
    [InlineData("ocean.row/256", true)]
    [InlineData("ocean.column/256", false)]
    public void Specialized_ocean_source_is_hashed_after_composition(string name, bool row)
    {
        string source = row ? OceanComputeShaders.RowPass(256) : OceanComputeShaders.ColumnPass(256);
        Assert.True(RendererPerfExecutable.ShaderHashes.ContainsKey(name), "Specialized GPU source is absent: " + name);
        Assert.Equal(Hash(source), RendererPerfExecutable.ShaderHashes[name]);
    }

    static string Hash(string source) => RendererPerfRecipe.Hash(Encoding.UTF8.GetBytes(source));
    static string Digest(IEnumerable<KeyValuePair<string, string>> sources)
        => RendererPerfRecipe.Digest(new SortedDictionary<string, string>(sources.ToDictionary(p => p.Key, p => Hash(p.Value)), StringComparer.Ordinal));
}
