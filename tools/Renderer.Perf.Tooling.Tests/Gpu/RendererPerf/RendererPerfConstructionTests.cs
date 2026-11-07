using System;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("AllocSensitive")]
public sealed partial class RendererPerfConstructionTests(ITestOutputHelper output)
{
    readonly ITestOutputHelper _output = output;
    internal static RendererPerfManifest Manifest(string preset = "pilot") => RendererPerfManifest.Parse(
        Encoding.UTF8.GetBytes(RendererPerfProtocolTests.ManifestJson(preset).ToJsonString()));

    [Fact]
    public void Mutable_inputs_and_disposed_originals_do_not_change_replay_values()
    {
        var capture = new RendererPerfConstructionCapture(Manifest());
        capture.BeginOperation(0);
        using var native = new RendererPerfConstructionFixture();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000);
        var device = RendererPerfDevice.Wrap(native, recorder, capture);
        GpuResourceLayoutElement[] elements = [new("Original", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, true)];
        var layout = device.Factory.CreateResourceLayout(new(elements));
        elements[0] = new("Mutated", GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        var shader = device.Factory.CreateShadersFromSpirv("vertex source", "fragment source");
        GpuVertexElement[] vertices = [new("Position", GpuVertexElementFormat.Float3)];
        GpuBlendAttachment[] blends = [GpuBlendAttachment.AlphaBlend];
        GpuPixelFormat[] colours = [GpuPixelFormat.R8G8B8A8UNorm];
        var description = new GpuPipelineDescription
        {
            ShaderSet = shader,
            ResourceLayouts = [layout],
            VertexLayouts = [new(12, 1, vertices)],
            BlendAttachments = blends,
            Outputs = new(null, colours),
            Topology = GpuPrimitiveTopology.TriangleList
        };
        var pipeline = device.Factory.CreateGraphicsPipeline(description);
        vertices[0] = new("Changed", GpuVertexElementFormat.Float4);
        description.VertexLayouts.Clear(); blends[0] = GpuBlendAttachment.OverrideBlend; colours[0] = GpuPixelFormat.R8UNorm;
        var commands = device.Factory.CreateCommandList(); commands.SetPipeline(pipeline);
        Assert.Same(pipeline, native.LastGraphicsBind);
        pipeline.Dispose(); shader.Dispose(); layout.Dispose();
        capture.EndOperation();
        var inventory = capture.Freeze();
        Assert.Equal("Original", Assert.Single(inventory.Layouts).Elements[0].Name);
        var graph = Assert.Single(inventory.Recipes).Graphics!;
        Assert.Equal("Position", Assert.Single(graph.VertexLayouts).Elements[0].Name);
        Assert.True(graph.BlendAttachments[0].BlendEnabled);
        Assert.Equal(GpuPixelFormat.R8G8B8A8UNorm, graph.Colour[0]);
        Assert.Equal(1, Assert.Single(inventory.Occurrences).BindCount);
        Assert.Equal(3, native.FactoryAttempts);
        Assert.Equal(3, inventory.Counts.FactoryAttempts);
        Assert.Same(inventory, capture.Freeze());
        _output.WriteLine("forwarded=3 events={0} occurrences={1}", inventory.FactoryEvents.Length, inventory.Occurrences.Length);
    }
    [Fact]
    public void Canonical_recipe_bytes_match_independent_declared_width_golden()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var pipeline = Compute(device, "x"); capture.EndOperation(); var inventory = capture.Freeze();
        byte[] bytes = RendererPerfConstructionEncoding.Bytes(inventory.Recipes[0], inventory.Shaders, inventory.Layouts);
        const string expected =
            "2a0000006b68616f7a656e67696e652e72656e64657265722d636f6e737472756374696f6e2d7265636970652f31" +
            "010000000100000007000000636f6d70757465040000006d61696e0100000078010000000100000001000000000000000001" +
            "4000000036343032366465366263396262373831346631343761366236613837623436333638353434386565666465343261386435386565616139313961383133613836" +
            "260000006b68616f7a656e67696e652e706970656c696e652d636f64652d67656e65726174696f6e2f310000000000";
        Assert.Equal(Convert.FromHexString(expected), bytes);
        Assert.Equal(211, bytes.Length);
        Assert.Equal("d429bdbabcccace8387050381fae8abb3fe79a8717261cceb19e93f29166227a", inventory.Recipes[0].RecipeSha256);
        _output.WriteLine("independent canonical bytes=211 sha256={0}", inventory.Recipes[0].RecipeSha256);
        Assert.Same(inventory, capture.Freeze());
        Assert.Throws<InvalidOperationException>(() => capture.BeginOperation(0));
        Assert.Throws<InvalidOperationException>(() => device.Factory.CreateComputeShaderFromSpirv("later"));
        Assert.Equal(2, native.FactoryAttempts);
    }

}
