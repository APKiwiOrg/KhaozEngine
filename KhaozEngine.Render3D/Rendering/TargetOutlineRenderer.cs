using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TargetOutlineRenderer : IDisposable
{
    const int DrawPayloadBytes = 160;
    const int DrawSlotBytes = 256;

    readonly IGpuDevice _gd;
    readonly GpuOutputDescription _targetOutputs;
    readonly IGpuShaderSet _fullShaders;
    readonly IGpuShaderSet _visibleShaders;
    readonly IGpuShaderSet _compositeShaders;
    readonly IGpuResourceLayout _drawLayout;
    readonly IGpuResourceLayout _materialLayout;
    readonly IGpuResourceLayout _compositeLayout;
    readonly List<IGpuBuffer> _compositeUbos = new();
    readonly List<IGpuResourceSet> _compositeSets = new();
    readonly IGpuTexture _white;
    readonly IGpuResourceSet _defaultMaterialSet;
    readonly List<QueuedDraw> _queue = new();
    readonly List<IDisposable> _retired = new();

    IGpuBuffer? _drawUbo;
    IGpuResourceSet? _drawSet;
    byte[] _drawImage = Array.Empty<byte>();
    int _capacity;
    Matrix4x4 _viewProj;

    IGpuTexture? _fullCoverage;
    IGpuTexture? _visibleCoverage;
    IGpuTexture? _visibleDepth;
    IGpuTexture? _msFullCoverage;
    IGpuTexture? _msVisibleCoverage;
    IGpuTexture? _msVisibleDepth;
    IGpuTexture? _privateDepth;
    IGpuTexture? _boundSceneDepth;
    IGpuFramebuffer? _fullFramebuffer;
    IGpuFramebuffer? _visibleFramebuffer;
    IGpuPipeline? _fullPipeline;
    IGpuPipeline? _visiblePipeline;
    IGpuPipeline? _skinnedFullPipeline;
    IGpuPipeline? _skinnedVisiblePipeline;
    IGpuPipeline _compositePipeline;
    int _resourceGeneration = -1;

    public TargetOutlineRenderer(IGpuDevice gd, GpuOutputDescription targetOutputs)
    {
        _gd = gd;
        _targetOutputs = targetOutputs;
        var f = gd.Factory;
        _fullShaders = f.CreateShadersFromSpirv(ShaderSources.TargetOutlineMaskVert,
            ShaderSources.TargetOutlineFullMaskFrag);
        _visibleShaders = f.CreateShadersFromSpirv(ShaderSources.TargetOutlineMaskVert,
            ShaderSources.TargetOutlineVisibleMaskFrag);
        _skinnedFullShaders = f.CreateShadersFromSpirv(ShaderSources.TargetOutlineSkinnedMaskVert,
            ShaderSources.TargetOutlineFullMaskFrag);
        _skinnedVisibleShaders = f.CreateShadersFromSpirv(ShaderSources.TargetOutlineSkinnedMaskVert,
            ShaderSources.TargetOutlineVisibleMaskFrag);
        _compositeShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
            ShaderSources.TargetOutlineCompositeFrag);
        _drawLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Draw", GpuResourceKind.UniformBuffer,
                GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));
        _materialLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Albedo", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        _compositeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("FullCoverage", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("VisibleCoverage", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("VisibleDepth", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("SceneDepth", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("PointSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("LinearSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Composite", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
        _white = f.CreateTexture(GpuTextureDescription.Texture2D(1, 1, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.Sampled));
        gd.UpdateTexture(_white, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1);
        _defaultMaterialSet = CreateMaterialSet(_white);
        _skinning = new TargetOutlineSkinningStore(gd);
        _compositePipeline = BuildCompositePipeline(f);
    }

    public IGpuResourceSet CreateMaterialSet(IGpuTexture albedo) =>
        _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_materialLayout, albedo, _gd.LinearSampler));

    public void EnsureCapacity(int drawCount)
    {
        if (_drawUbo != null && _capacity >= drawCount) return;
        int freshCapacity = Math.Max(drawCount, _capacity == 0 ? 4 : _capacity * 2);
        var freshImage = new byte[freshCapacity * DrawSlotBytes];
        _drawImage.AsSpan().CopyTo(freshImage);
        IGpuBuffer freshUbo = _gd.Factory.CreateBuffer(new GpuBufferDescription(
            (uint)(freshCapacity * DrawSlotBytes), GpuBufferUsage.UniformBuffer));
        IGpuResourceSet freshSet;
        try
        {
            freshSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_drawLayout,
                new GpuBufferRange(freshUbo, 0, DrawPayloadBytes)));
        }
        catch
        {
            freshUbo.Dispose();
            throw;
        }

        if (_drawUbo != null) _retired.Add(_drawUbo);
        if (_drawSet != null) _retired.Add(_drawSet);
        _capacity = freshCapacity;
        _drawUbo = freshUbo;
        _drawSet = freshSet;
        _drawImage = freshImage;
    }

    public void BeginGroup(Matrix4x4 clipViewProjection)
    {
        _viewProj = clipViewProjection;
        _queue.Clear();
        BeginSkinnedGroup();
    }

    public void EnqueueRigid(IGpuBuffer vertexBuffer, IGpuBuffer indexBuffer, int indexCount,
        GpuIndexFormat indexFormat, IGpuResourceSet? materialSet, int drawIndex, Matrix4x4 world,
        float alphaCutoff, float dissolve, bool dissolveComplement, Vector3 renderOrigin)
    {
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin);
        _queue.Add(new QueuedDraw(QueuedGeometry.Rigid, vertexBuffer, indexBuffer, indexCount, indexFormat,
            materialSet ?? _defaultMaterialSet, drawIndex, -1, 0, 0, 0));
    }

    public bool HasQueuedDraws => _queue.Count > 0;

    public void Render(IGpuCommandList cl, RenderResources resources, IGpuFramebuffer target,
        Color color, float widthPixels, float backgroundDepth, bool pixelated, int styleIndex, bool occluded)
    {
        if (_queue.Count == 0) return;
        PrepareGpuPalette(cl);
        BindTargets(resources);
        cl.UpdateBuffer(_drawUbo!, 0, (ReadOnlySpan<byte>)_drawImage);

        cl.SetFramebuffer(_fullFramebuffer!);
        cl.ClearColorTarget(0, Color.Transparent);
        cl.ClearDepthStencil(1f);
        DrawQueue(cl, full: true);
        if (resources.Msaa)
            cl.ResolveTexture(_msFullCoverage!, _fullCoverage!);
        if (_fullCoverage!.MipLevels > 1) cl.GenerateMipmaps(_fullCoverage);

        // A border nothing hides reads only the full union, so the scene-depth visible pass is skipped.
        if (occluded)
        {
            cl.SetFramebuffer(_visibleFramebuffer!);
            cl.ClearColorTarget(0, Color.Transparent);
            cl.ClearColorTarget(1, new Color(backgroundDepth, 0f, 0f, 0f));
            DrawQueue(cl, full: false);
            if (resources.Msaa)
            {
                cl.ResolveTexture(_msVisibleCoverage!, _visibleCoverage!);
                cl.ResolveTexture(_msVisibleDepth!, _visibleDepth!);
            }
            if (_visibleCoverage!.MipLevels > 1) cl.GenerateMipmaps(_visibleCoverage);
        }

        var composite = new CompositeUbo
        {
            OutlineColor = color.ToVector4(),
            Params = new Vector4(1f / target.Width, 1f / target.Height, widthPixels, backgroundDepth),
            Mode = new Vector4(pixelated ? 1f : 0f,
                pixelated ? 0f : MathF.Max(0f, MathF.Max(target.Width / (float)resources.Width,
                    target.Height / (float)resources.Height) - 1f),
                _visibleCoverage!.MipLevels > 1
                    ? MathF.Max(0f, MathF.Log2(MathF.Max(resources.Width / (float)target.Width,
                        resources.Height / (float)target.Height))) : 0f,
                occluded ? 0f : 1f),
        };
        EnsureCompositeSlot(styleIndex);
        cl.UpdateBuffer(_compositeUbos[styleIndex], 0, in composite);
        cl.SetFramebuffer(target);
        cl.SetPipeline(_compositePipeline);
        cl.SetGraphicsResourceSet(0, _compositeSets[styleIndex]);
        cl.Draw(3);
    }

    void DrawQueue(IGpuCommandList cl, bool full)
    {
        foreach (QueuedDraw draw in _queue)
        {
            IGpuPipeline pipeline = draw.Geometry == QueuedGeometry.GpuSkinned
                ? full ? _skinnedFullPipeline! : _skinnedVisiblePipeline!
                : full ? _fullPipeline! : _visiblePipeline!;
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, _drawSet!, (uint)(draw.DrawIndex * DrawSlotBytes));
            cl.SetGraphicsResourceSet(1, draw.MaterialSet);
            if (draw.Geometry == QueuedGeometry.GpuSkinned)
                cl.SetGraphicsResourceSet(2, _skinning.PaletteSet,
                    (uint)draw.PaletteSlot * TargetOutlineSkinningStore.PaletteSlotBytes);
            cl.SetVertexBuffer(0, draw.VertexBuffer);
            cl.SetIndexBuffer(draw.IndexBuffer, draw.IndexFormat);
            cl.DrawIndexed((uint)draw.IndexCount, 1, 0, draw.BaseVertex, 0);
        }
    }

    void BindTargets(RenderResources resources)
    {
        if (_resourceGeneration == resources.Generation) return;
        DisposeTargets();
        try
        {
            CreateTargets(resources);
            _resourceGeneration = resources.Generation;
        }
        catch
        {
            DisposeTargets();
            _resourceGeneration = -1;
            throw;
        }
    }

    void CreateTargets(RenderResources resources)
    {
        var f = _gd.Factory;
        uint width = (uint)resources.Width;
        uint height = (uint)resources.Height;
        uint samples = (uint)resources.SampleCount;
        uint coverageMips = resources.Mipped ? SplatMaterialConfig.MipLevelCount(resources.Width,
            resources.Height) : 1u;
        var sampledTarget = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
        var coverageUsage = resources.Mipped ? sampledTarget | GpuTextureUsage.GenerateMipmaps : sampledTarget;
        _fullCoverage = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.R8UNorm,
            coverageUsage, coverageMips, 1, 1));
        _visibleCoverage = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.R16G16Float,
            coverageUsage, coverageMips, 1, 1));
        _visibleDepth = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.R32Float,
            sampledTarget, 1, 1, 1));
        _privateDepth = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.D32FloatS8UInt,
            GpuTextureUsage.DepthStencil, 1, 1, samples));

        IGpuTexture fullCoverageTarget = _fullCoverage;
        IGpuTexture visibleTarget = _visibleCoverage;
        IGpuTexture visibleDepthTarget = _visibleDepth;
        if (resources.Msaa)
        {
            _msFullCoverage = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.R8UNorm,
                GpuTextureUsage.RenderTarget, 1, 1, samples));
            _msVisibleCoverage = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.R16G16Float,
                GpuTextureUsage.RenderTarget, 1, 1, samples));
            _msVisibleDepth = f.CreateTexture(new GpuTextureDescription(width, height, GpuPixelFormat.R32Float,
                GpuTextureUsage.RenderTarget, 1, 1, samples));
            fullCoverageTarget = _msFullCoverage;
            visibleTarget = _msVisibleCoverage;
            visibleDepthTarget = _msVisibleDepth;
        }

        _fullFramebuffer = f.CreateFramebuffer(_privateDepth, fullCoverageTarget);
        _visibleFramebuffer = f.CreateFramebuffer(resources.DepthStencil, visibleTarget, visibleDepthTarget);
        _boundSceneDepth = resources.DepthColorTex;
        _fullPipeline = BuildMaskPipeline(f, _fullShaders, _fullFramebuffer.Outputs, full: true);
        _visiblePipeline = BuildMaskPipeline(f, _visibleShaders, _visibleFramebuffer.Outputs, full: false);
        _skinnedFullPipeline = BuildSkinnedMaskPipeline(f, _skinnedFullShaders,
            _fullFramebuffer.Outputs, full: true);
        _skinnedVisiblePipeline = BuildSkinnedMaskPipeline(f, _skinnedVisibleShaders,
            _visibleFramebuffer.Outputs, full: false);
    }

    void EnsureCompositeSlot(int styleIndex)
    {
        while (_compositeUbos.Count <= styleIndex)
            _compositeUbos.Add(_gd.Factory.CreateBuffer(new GpuBufferDescription(48,
                GpuBufferUsage.UniformBuffer)));
        while (_compositeSets.Count <= styleIndex)
        {
            int index = _compositeSets.Count;
            _compositeSets.Add(_gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout,
                _fullCoverage!, _visibleCoverage!, _visibleDepth!, _boundSceneDepth!, _gd.PointSampler,
                _gd.LinearSampler,
                _compositeUbos[index])));
        }
    }

    IGpuPipeline BuildMaskPipeline(IGpuResourceFactory factory, IGpuShaderSet shaders,
        GpuOutputDescription outputs, bool full)
    {
        var vertexLayout = new GpuVertexLayoutDescription(
            new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
            new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
            new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
        return factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = full
                ? new[] { GpuBlendAttachment.OverrideBlend }
                : new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
            DepthStencil = full ? GpuDepthStencilState.DepthOnlyLessEqual
                : GpuDepthStencilState.DepthTestLessEqualNoWrite,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _drawLayout, _materialLayout },
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
            Outputs = outputs,
        });
    }

    IGpuPipeline BuildCompositePipeline(IGpuResourceFactory factory) =>
        factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _compositeLayout },
            ShaderSet = _compositeShaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>(),
            Outputs = _targetOutputs,
        });

    void DisposeTargets()
    {
        if (_fullFramebuffer != null) _gd.WaitForIdle();
        foreach (IGpuResourceSet set in _compositeSets) set.Dispose();
        _compositeSets.Clear();
        _fullPipeline?.Dispose();
        _visiblePipeline?.Dispose();
        _skinnedFullPipeline?.Dispose();
        _skinnedVisiblePipeline?.Dispose();
        _fullFramebuffer?.Dispose();
        _visibleFramebuffer?.Dispose();
        _fullCoverage?.Dispose();
        _visibleCoverage?.Dispose();
        _visibleDepth?.Dispose();
        _msFullCoverage?.Dispose();
        _msVisibleCoverage?.Dispose();
        _msVisibleDepth?.Dispose();
        _privateDepth?.Dispose();
        _fullPipeline = null;
        _visiblePipeline = null;
        _skinnedFullPipeline = null;
        _skinnedVisiblePipeline = null;
        _fullFramebuffer = null;
        _visibleFramebuffer = null;
        _fullCoverage = null;
        _visibleCoverage = null;
        _visibleDepth = null;
        _msFullCoverage = null;
        _msVisibleCoverage = null;
        _msVisibleDepth = null;
        _privateDepth = null;
        _boundSceneDepth = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawUbo
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 World;
        public Vector4 Params;
        public Vector4 RenderOrigin;
    }

    struct CompositeUbo
    {
        public Vector4 OutlineColor;
        public Vector4 Params;
        public Vector4 Mode;
    }

    enum QueuedGeometry
    {
        Rigid,
        GpuSkinned,
        CpuSkinned,
    }

    readonly record struct QueuedDraw(
        QueuedGeometry Geometry,
        IGpuBuffer VertexBuffer,
        IGpuBuffer IndexBuffer,
        int IndexCount,
        GpuIndexFormat IndexFormat,
        IGpuResourceSet MaterialSet,
        int DrawIndex,
        int PaletteSlot,
        int BaseVertex,
        int PoseStart,
        int PoseCount);

    public void Dispose()
    {
        DisposeTargets();
        _defaultMaterialSet.Dispose();
        _white.Dispose();
        _drawSet?.Dispose();
        _drawUbo?.Dispose();
        foreach (IDisposable resource in _retired) resource.Dispose();
        _compositePipeline.Dispose();
        _skinning.Dispose();
        foreach (IGpuBuffer buffer in _compositeUbos) buffer.Dispose();
        _drawLayout.Dispose();
        _materialLayout.Dispose();
        _compositeLayout.Dispose();
        _fullShaders.Dispose();
        _visibleShaders.Dispose();
        _skinnedFullShaders.Dispose();
        _skinnedVisibleShaders.Dispose();
        _compositeShaders.Dispose();
    }
}
