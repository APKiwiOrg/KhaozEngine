using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    IGpuResourceLayout? _coverageCascadeLayout;
    IGpuBuffer? _coverageCascadeUniform;
    IGpuResourceSet? _coverageCascadeSet;
    byte[] _coverageCascadeImage = [];
    IGpuShaderSet[] _coverageCascadeShaders = [];
    IGpuPipeline[] _coverageCascadePipelines = [];
    int _coverageCascadeStride;

    void ReserveCoverageShadows(int count)
    {
        if (_coverageCascadeLayout is null)
        {
            var factory = _gd.Factory;
            _coverageCascadeLayout = factory.CreateResourceLayout(new(new GpuResourceLayoutElement(
                "VBlock", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
            _coverageCascadeShaders = [
                factory.CreateShadersFromSpirv(ShaderSources.CoverageCascadeVert, ShaderSources.ShadowDepthFrag, new(PreciseMath: true)),
                factory.CreateShadersFromSpirv(ShaderSources.CoverageCascadeDissolveVert, ShaderSources.ShadowDepthDissolveFrag, new(PreciseMath: true)),
                factory.CreateShadersFromSpirv(ShaderSources.CoverageCascadeDissolveVert, ShaderSources.ShadowDepthDissolveInvertedFrag, new(PreciseMath: true))];
            _coverageCascadePipelines = new IGpuPipeline[3];
            for (int i = 0; i < 3; i++)
                _coverageCascadePipelines[i] = factory.CreateGraphicsPipeline(new()
                {
                    BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                    DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                    Rasterizer = new(GpuFaceCull.Front, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, true),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    ResourceLayouts = [_coverageCascadeLayout, _coveragePull.Layout],
                    ShaderSet = _coverageCascadeShaders[i], VertexLayouts = [], Outputs = _shadowMap.Outputs,
                });
        }
        int bytes = checked(Math.Max(1, count) * ShadowSettings.MaxCascades * 256);
        if (_coverageCascadeImage.Length >= bytes) return;
        var next = _gd.Factory.CreateBuffer(new((uint)bytes, GpuBufferUsage.UniformBuffer));
        IGpuResourceSet set;
        try { set = _gd.Factory.CreateResourceSet(new(_coverageCascadeLayout, new GpuBufferRange(next, 0, 160))); }
        catch { next.Dispose(); throw; }
        _retired.Retire(_coverageCascadeSet); _retired.Retire(_coverageCascadeUniform);
        _coverageCascadeSet = set; _coverageCascadeUniform = next; _coverageCascadeImage = new byte[bytes];
    }

    internal void PackCoverageCascade(in CoveragePulledDraw draw, in InstanceData instance, int cascade,
        int count, in Matrix4x4 depth, Vector3 origin, float noiseScale)
    {
        _coverageCascadeStride = count;
        var slot = _coverageCascadeImage.AsSpan(checked((cascade * count + (int)draw.InstanceSlot) * 256), 256);
        Matrix4x4 mvp = instance.Model * depth;
        var renderOrigin = new Vector4(origin, 0);
        var dissolve = new Vector4(noiseScale, instance.Dissolve.X, instance.DissolveComplement, 0);
        MemoryMarshal.Write(slot, in mvp); MemoryMarshal.Write(slot[64..], in instance.Model);
        MemoryMarshal.Write(slot[128..], in renderOrigin); MemoryMarshal.Write(slot[144..], in dissolve);
    }

    internal void UploadCoverageCascades(IGpuCommandList commands)
        => commands.UpdateBuffer(_coverageCascadeUniform!, 0, (ReadOnlySpan<byte>)_coverageCascadeImage);

    internal void DrawCoverageCascade(IGpuCommandList commands, in CoveragePulledDraw draw, int cascade,
        bool dissolve, bool complement)
    {
        commands.SetPipeline(_coverageCascadePipelines[dissolve ? complement ? 2 : 1 : 0]);
        _shadowMap.SetCascadeScissor(commands, cascade);
        commands.SetGraphicsResourceSet(0, _coverageCascadeSet!, checked((uint)(cascade * _coverageCascadeStride + draw.InstanceSlot) * 256));
        _coveragePull.Bind(commands, 1, draw.InstanceSlot);
        CoveragePullObserverForTests?.Invoke("cascade", draw);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
    }

    void DisposeCoverageShadows()
    {
        foreach (var pipeline in _coverageCascadePipelines) pipeline.Dispose();
        foreach (var shaders in _coverageCascadeShaders) shaders.Dispose();
        _coverageCascadeSet?.Dispose(); _coverageCascadeUniform?.Dispose(); _coverageCascadeLayout?.Dispose();
    }
}
