using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class SlangReflectionTests
{
    [Fact]
    public void Joins_the_sprite_reflection_into_three_bindings()
    {
        var bindings = SlangReflection.Join(Recorded("spirv"), Recorded("metal"), Recorded("hlsl"));

        Assert.Equal(
            new[]
            {
                new BundleBinding("Tex", 0, 0, BundleResourceKind.TextureReadOnly, 0, 0),
                new BundleBinding("Samp", 0, 1, BundleResourceKind.Sampler, 0, 0),
                new BundleBinding("Vp", 1, 0, BundleResourceKind.UniformBuffer, 0, 0),
            },
            bindings);
    }

    [Fact]
    public void Refuses_a_metal_buffer_index_in_the_stream_range()
    {
        Assert.Equal(15, Assert.Single(SlangReflection.Join(SpirvBuffer, MetalBuffer(15), HlslBuffer)).MetalIndex);

        var refusal = Assert.Throws<ShaderBuildException>(() => SlangReflection.Join(SpirvBuffer, MetalBuffer(16), HlslBuffer));

        Assert.Equal(ShaderBuildFailure.MetalIndexInStreamRange, refusal.Failure);
        Assert.Equal("Vp", refusal.Parameter);
    }

    [Theory]
    [InlineData("""{ "kind": "resource", "baseShape": "texture1D" }""")]
    [InlineData("""{ "kind": "resource", "baseShape": "texture2D", "array": true }""")]
    [InlineData("""{ "kind": "resource", "baseShape": "texture2D", "multisample": true }""")]
    [InlineData("""{ "kind": "resource", "baseShape": "texture3D" }""")]
    [InlineData("""{ "kind": "resource", "baseShape": "textureCube", "array": true }""")]
    public void Joins_every_sampled_texture_shape_as_a_read_only_texture(string type)
    {
        var binding = Assert.Single(JoinShaderResource("Tex", type));

        Assert.Equal(BundleResourceKind.TextureReadOnly, binding.Kind);
    }

    [Theory]
    [InlineData("""{ "kind": "resource", "baseShape": "structuredBuffer" }""")]
    [InlineData("""{ "kind": "resource", "baseShape": "textureBuffer" }""")]
    public void Refuses_a_parameter_kind_outside_the_map(string type)
    {
        var refusal = Assert.Throws<ShaderBuildException>(() => JoinShaderResource("Items", type));

        Assert.Equal(ShaderBuildFailure.ManifestInvalid, refusal.Failure);
        Assert.Equal("Items", refusal.Parameter);
    }

    private static IReadOnlyList<BundleBinding> JoinShaderResource(string name, string type)
        => SlangReflection.Join(
            Reflection(name, """{ "kind": "descriptorTableSlot", "index": 0 }""", type),
            Reflection(name, """{ "kind": "shaderResource", "index": 0 }""", type),
            Reflection(name, """{ "kind": "shaderResource", "index": 0 }""", type));

    private const string BufferType = """{ "kind": "constantBuffer" }""";
    private static readonly string SpirvBuffer = Reflection("Vp", """{ "kind": "descriptorTableSlot", "space": 1, "index": 0 }""", BufferType);
    private static readonly string HlslBuffer = Reflection("Vp", """{ "kind": "constantBuffer", "index": 0 }""", BufferType);

    private static string MetalBuffer(int index) => Reflection("Vp", $$"""{ "kind": "constantBuffer", "index": {{index}} }""", BufferType);

    private static string Reflection(string name, string binding, string type)
        => $$"""{ "parameters": [ { "name": "{{name}}", "binding": {{binding}}, "type": {{type}} } ] }""";

    private static string Recorded(string target)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reflection", $"sprite.vertex.{target}.json"));
}
