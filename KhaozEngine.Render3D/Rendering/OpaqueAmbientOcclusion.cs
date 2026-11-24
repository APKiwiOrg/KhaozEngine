using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The optional opaque MRT and its two fullscreen passes, scoped to the source targets.</summary>
internal sealed class OpaqueAmbientOcclusion : IDisposable
{
    const GpuPixelFormat HorizonFormat = GpuPixelFormat.R8UNorm;
    static readonly GpuBlendAttachment SubtractRgb = new(true,
        GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.ReverseSubtract,
        GpuBlendFactor.Zero, GpuBlendFactor.One, GpuBlendFunction.Add);

    readonly List<IDisposable> _owned = new();
    readonly IGpuFramebuffer _horizonTarget;
    readonly IGpuPipeline _horizon, _composite;
    readonly IGpuResourceSet _horizonSet, _compositeSet;
    readonly IGpuBuffer _uniforms;

    internal IGpuFramebuffer ModelFramebuffer { get; }
    internal IGpuFramebuffer BlobFramebuffer { get; }
    internal long TextureBytes { get; }

    record struct Uniforms(Matrix4x4 InverseProjection, Matrix4x4 View, Vector4 ProjectionScale, Vector4 Parameters);

    internal OpaqueAmbientOcclusion(IGpuDevice device, RenderResources targets)
    {
        var factory = device.Factory;
        uint w = (uint)targets.Width, h = (uint)targets.Height;
        uint halfW = (w + 1) / 2, halfH = (h + 1) / 2;
        TextureBytes = (long)w * h * 8 + (long)halfW * halfH;
        try
        {
            var diffuse = Own(MakeTarget(w, h, AmbientDiffuseTarget.Format));
            ModelFramebuffer = Own(targets.CreateModelFramebuffer(targets.ColorTex, targets.NormalTex, targets.DepthColorTex, diffuse));
            BlobFramebuffer = Own(factory.CreateFramebuffer(targets.DepthStencil, targets.ColorTex, diffuse));
            var occlusion = Own(MakeTarget(halfW, halfH, HorizonFormat));
            _horizonTarget = Own(factory.CreateFramebuffer(null, occlusion));
            _uniforms = Own(factory.CreateBuffer(new GpuBufferDescription(160, GpuBufferUsage.UniformBuffer)));
            var horizonLayout = Own(factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("DeviceDepth"), T("GeometryNormal"), T("Diffuse"), S("Point"), U("AoFrame"))));
            var compositeLayout = Own(factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("DeviceDepth"), T("GeometryNormal"), T("Diffuse"), S("Point"), U("AoFrame"), T("Occlusion"))));
            _horizonSet = Own(factory.CreateResourceSet(new GpuResourceSetDescription(horizonLayout,
                targets.DepthColorTex, targets.NormalTex, diffuse, device.PointSampler, _uniforms)));
            _compositeSet = Own(factory.CreateResourceSet(new GpuResourceSetDescription(compositeLayout,
                targets.DepthColorTex, targets.NormalTex, diffuse, device.PointSampler, _uniforms, occlusion)));
            var horizonShaders = Own(factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.AmbientOcclusionHorizon));
            var compositeShaders = Own(factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.AmbientOcclusionComposite));
            _horizon = Own(TemporalResolveRenderer.Fullscreen(factory, horizonShaders, horizonLayout, _horizonTarget.Outputs));
            _composite = Own(TemporalResolveRenderer.Fullscreen(factory, compositeShaders, [compositeLayout],
                targets.ColorDepthFB.Outputs, SubtractRgb));
            _horizon.EnsureDeviceReady();
            _composite.EnsureDeviceReady();
        }
        catch
        {
            Dispose();
            throw;
        }

        IGpuTexture MakeTarget(uint width, uint height, GpuPixelFormat format) =>
            factory.CreateTexture(GpuTextureDescription.Texture2D(width, height, format,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
    }

    internal bool Prepare(IGpuCommandList cl, in FrameView view, AmbientOcclusionSettings settings, float backgroundDepth)
    {
        if (!Matrix4x4.Invert(view.JitteredProjection, out Matrix4x4 inverse)) return false;
        var scale = new Vector4(
            MathF.Abs(view.Projection.M11) * view.Width * 0.5f,
            MathF.Abs(view.Projection.M22) * view.Height * 0.5f,
            view.IsOrthographic ? 0f : MathF.Abs(view.Projection.M34), 0f);
        var frame = new Uniforms(inverse, view.View, scale, new Vector4(settings.RadiusMeters, settings.Strength, backgroundDepth, 0f));
        cl.UpdateBuffer(_uniforms, 0, in frame);
        return true;
    }

    internal void DrawHorizon(IGpuCommandList cl) => Draw(cl, _horizonTarget, _horizon, _horizonSet);

    internal void DrawComposite(IGpuCommandList cl, IGpuFramebuffer output) => Draw(cl, output, _composite, _compositeSet);

    static void Draw(IGpuCommandList cl, IGpuFramebuffer output, IGpuPipeline pipeline, IGpuResourceSet set)
    {
        cl.SetFramebuffer(output);
        cl.SetPipeline(pipeline);
        cl.SetGraphicsResourceSet(0, set);
        cl.Draw(3);
    }

    TResource Own<TResource>(TResource resource) where TResource : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; --i) _owned[i].Dispose();
        _owned.Clear();
    }
}
