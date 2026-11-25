using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class GroundDecalRenderer
{
    IGpuShaderSet? _ambientBlobShaders;
    IGpuPipeline? _ambientBlobPipeline;

    internal void EnsureAmbientBlobPipeline(in GpuOutputDescription outputs)
    {
        if (_ambientBlobPipeline is not null) return;
        // Early blob shadows alpha blend over opaque ground before characters. Attenuate the stored
        // indirect contribution by the same factor so AO remains a subtraction of the surviving light.
        string fragment = ShaderText.After(ShaderSources.DecalFrag, "layout(location=0) out vec4 oColor;",
            "\nlayout(location=1) out vec4 oAmbient;");
        fragment = ShaderText.BeforeEndOfMain(fragment, "    oAmbient = vec4(0.0, 0.0, 0.0, oColor.a);\n");
        var shaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.DecalVert, fragment);
        IGpuPipeline? pipeline = null;
        try
        {
            GpuBlendAttachment attenuate = new(true, GpuBlendFactor.Zero, GpuBlendFactor.InverseSourceAlpha,
                GpuBlendFunction.Add, GpuBlendFactor.Zero, GpuBlendFactor.One, GpuBlendFunction.Add);
            pipeline = Pipe(_gd.Factory, outputs, GpuBlendAttachment.AlphaBlend, GpuComparison.Greater, shaders, attenuate);
            pipeline.EnsureDeviceReady();
            _ambientBlobShaders = shaders;
            _ambientBlobPipeline = pipeline;
        }
        catch
        {
            pipeline?.Dispose();
            shaders.Dispose();
            throw;
        }
    }

    internal void ReleaseAmbientBlobPipeline()
    {
        if (_ambientBlobPipeline is null) return;
        _retired.Retire(_ambientBlobPipeline, _ambientBlobShaders, null);
        _ambientBlobShaders = null;
        _ambientBlobPipeline = null;
    }
}
