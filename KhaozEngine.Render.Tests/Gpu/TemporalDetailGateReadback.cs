using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Replays the classifier before flag packing, preserving full motion and trust observations without feedback.</summary>
internal sealed class TemporalDetailGateReadback : IDisposable
{
    readonly TemporalFixture _fixture;
    readonly IGpuTexture _pixels;
    readonly IGpuFramebuffer _target;
    readonly IGpuCommandList _commands;
    readonly IGpuResourceLayout _layout;
    readonly TemporalVisibilityPipelinePair _programs;
    readonly IGpuResourceSet[] _sets = new IGpuResourceSet[2];

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
            new GpuResourceLayoutElement("DetailColor", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));
        var renderer = fixture.Scene.TemporalResolveRendererForTests!;
        string source = ShaderSources.TemporalDetailClassifyFragment(TemporalResolvePrecisionPolicy.For(fixture.Device));
        source = source[..source.LastIndexOf("void main()", StringComparison.Ordinal)] + @"
void main() {
    temporalResolvePixel(ivec2(gl_FragCoord.xy));
    oClassification = detailClassification + vec4(vUv.x * 1.0e-30);
}";
        for (int write = 0; write < 2; write++)
            _sets[write] = factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
                fixture.Scene.TemporalHistory.DetailStore!.Color(1 - write)));
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
            _commands.SetGraphicsResourceSet(1, _sets[_fixture.Scene.TemporalHistory.WriteIndex]);
            _commands.Draw(3);
        }
        _fixture.Device.Submit(_commands);
        _fixture.Device.WaitForIdle();
        return TemporalTextureIo.Read(_fixture.Device, _pixels);
    }

    public void Dispose()
    {
        foreach (var set in _sets) set.Dispose();
        _programs.Dispose(); _layout.Dispose(); _commands.Dispose(); _target.Dispose(); _pixels.Dispose();
    }
}
