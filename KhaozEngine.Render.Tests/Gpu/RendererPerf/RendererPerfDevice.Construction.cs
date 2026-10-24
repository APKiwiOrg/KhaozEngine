using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal partial class RendererPerfDevice
{
    RendererPerfConstructionCapture? _capture;
    internal static IGpuDevice Wrap(IGpuDevice native, RendererPerfRecorder recorder, RendererPerfConstructionCapture capture)
    {
        var wrapper = CreateWrapper(native, recorder);
        wrapper._capture = capture; recorder.AttachCapture(capture); return wrapper;
    }
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
    {
        int token = _capture?.Layout(d) ?? -1;
        _capture?.Forwarded(RendererPerfFactoryKind.Layout);
        IGpuResourceLayout resource;
        try { resource = _native.Factory.CreateResourceLayout(d); }
        catch (Exception e) { _capture?.Failed(token, e); throw; }
        Created(); CaptureSuccess(token, resource, RendererPerfFactoryKind.Layout); return resource;
    }
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment)
    {
        int token = _capture?.Shader(vertex, fragment, Backend) ?? -1;
        _capture?.Forwarded(RendererPerfFactoryKind.Shader);
        IGpuShaderSet resource;
        try { resource = _native.Factory.CreateShadersFromSpirv(vertex, fragment); }
        catch (Exception e) { _capture?.Failed(token, e); throw; }
        Created(); CaptureSuccess(token, resource, RendererPerfFactoryKind.Shader); return resource;
    }
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source)
    {
        int token = _capture?.Shader(source, null, Backend) ?? -1;
        _capture?.Forwarded(RendererPerfFactoryKind.Shader);
        IGpuComputeShader resource;
        try { resource = _native.Factory.CreateComputeShaderFromSpirv(source); }
        catch (Exception e) { _capture?.Failed(token, e); throw; }
        Created(); CaptureSuccess(token, resource, RendererPerfFactoryKind.Shader); return resource;
    }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
    {
        int token = _capture?.Graphics(d) ?? -1;
        _capture?.Forwarded(RendererPerfFactoryKind.GraphicsPipeline);
        IGpuPipeline resource;
        try { resource = _native.Factory.CreateGraphicsPipeline(d); }
        catch (Exception e) { _capture?.Failed(token, e); throw; }
        Created(); int occurrence = CaptureSuccess(token, resource, RendererPerfFactoryKind.GraphicsPipeline);
        _recorder.Register(resource, new(true, Topology: d.Topology, OccurrenceId: occurrence < 0 ? null : occurrence));
        return resource;
    }
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
    {
        int token = _capture?.Compute(d) ?? -1;
        _capture?.Forwarded(RendererPerfFactoryKind.ComputePipeline);
        IGpuComputePipeline resource;
        try { resource = _native.Factory.CreateComputePipeline(d); }
        catch (Exception e) { _capture?.Failed(token, e); throw; }
        Created(); int occurrence = CaptureSuccess(token, resource, RendererPerfFactoryKind.ComputePipeline);
        _recorder.Register(resource, new(true, LocalX: d.Shader.ThreadGroupSizeX, LocalY: d.Shader.ThreadGroupSizeY,
            LocalZ: d.Shader.ThreadGroupSizeZ, OccurrenceId: occurrence < 0 ? null : occurrence));
        return resource;
    }
    int CaptureSuccess(int token, object resource, RendererPerfFactoryKind kind)
    {
        if (_capture is null) return -1;
        if (token >= 0) return _capture.Success(token, resource);
        _capture.OmittedSuccess(kind); return -1;
    }
}
