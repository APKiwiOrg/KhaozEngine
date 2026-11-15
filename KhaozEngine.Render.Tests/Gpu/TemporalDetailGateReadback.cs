using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Copies the live classifier with no feedback into history.</summary>
internal sealed class TemporalDetailGateReadback : IDisposable
{
    readonly TemporalFixture _fixture;
    readonly IGpuTexture _pixels;
    readonly IGpuFramebuffer _target;
    readonly IGpuCommandList _commands;
    readonly IGpuResourceLayout _layout;
    readonly TemporalVisibilityPipelinePair _programs;

    internal TemporalDetailGateReadback(TemporalFixture fixture)
    {
        _fixture = fixture;
        var factory = fixture.Device.Factory;
        _pixels = factory.CreateTexture(GpuTextureDescription.Texture2D((uint)fixture.DisplayWidth,
            (uint)fixture.DisplayHeight, GpuPixelFormat.R32G32B32A32Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _target = factory.CreateFramebuffer(null, _pixels);
        _commands = factory.CreateCommandList();
        _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new("DetailColor", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new("DetailClassification", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));
        var renderer = fixture.Scene.TemporalResolveRendererForTests!;
        string source = ShaderSources.TemporalDetailResolveFragment(TemporalResolvePrecisionPolicy.For(fixture.Device));
        source = source[..source.LastIndexOf("void main()", StringComparison.Ordinal)] + @"
void main() {
    // Keep the borrowed resolve layouts live on backends whose bindings are positional.
    // x and y are scratch resolve output. Attribution reads the classifier's exact z and w.
    TemporalPixel p = temporalDetailPixel(ivec2(gl_FragCoord.xy));
    oColor = texelFetch(sampler2D(DetailClassification, LinearClamp), ivec2(gl_FragCoord.xy), 0);
    oColor.xy = vec2(p.color.x + p.color.y, p.color.z + p.alpha + vUv.x * 1.0e-30);
}";
        foreach (string declaration in new[] { "layout(location=1) out vec4 oState;",
            "layout(location=2) out vec4 oMean;", "layout(location=3) out vec4 oTransport;" })
            source = source.Replace(declaration, "", StringComparison.Ordinal);
        _programs = new TemporalVisibilityPipelinePair(fixture.Device, source,
            [renderer.ResolveLayout, _layout], [renderer.FullyLitResolveLayout, _layout], _target.Outputs);
    }

    internal float[] Read()
    {
        var renderer = _fixture.Scene.TemporalResolveRendererForTests!;
        using (GpuRecording.Open(_fixture.Device, _commands, nameof(TemporalDetailGateReadback)))
        {
            var uniforms = renderer.LastUniforms;
            renderer.PrepareUniforms(_commands, uniforms,
                new TemporalDepthStoreUniforms { CurrentDepth = uniforms.CurrentDepth });
            _commands.SetFramebuffer(_target);
            _commands.SetPipeline(_programs.Select(false));
            _commands.SetGraphicsResourceSet(0, renderer.CurrentSet!);
            _commands.SetGraphicsResourceSet(1, _fixture.Scene.TemporalHistory.DetailStore!.ReadSet(
                _fixture.Scene.TemporalHistory.WriteIndex));
            _commands.Draw(3);
        }
        _fixture.Device.Submit(_commands);
        _fixture.Device.WaitForIdle();
        return TemporalTextureIo.Read(_fixture.Device, _pixels);
    }

    public void Dispose()
    {
        _programs.Dispose(); _layout.Dispose(); _commands.Dispose(); _target.Dispose(); _pixels.Dispose();
    }
}
