using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The caustic renderer's programs, atlases and buffers. Everything the frame path can outgrow is replaced
/// through the retire queue, because a submitted frame may still read it, and every replacement is built whole before
/// any current resource changes, so a failure leaves the last working state.</summary>
internal sealed partial class WaterCausticRenderer
{
    const uint FieldSide = WaterSurfaceWindow.Cells;
    const uint MapSide = (uint)(WaterSurfaceWindow.ExtentMetres / WaterSurfaceWindow.MapTexelMetres);
    const GpuPixelFormat AtlasFormat = GpuPixelFormat.R16G16B16A16Float;
    const GpuTextureUsage AtlasUsage = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
    const uint QuadVertices = 6;
    // Six vertices per cell with no index buffer: each vertex lands its whole triangle to give it an exact area ratio.
    const uint PatchVertexCount = WaterCausticPlan.PatchCells * WaterCausticPlan.PatchCells * 6;
    const int InitialSlots = 4;

    // The sheets and subtract quad add exact values on all four channels. The stock additive preset scales by source
    // alpha, which would weight every slice by the deepest slice's value.
    static readonly GpuBlendAttachment PlusOne = new(true,
        GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.Add,
        GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.Add);

    readonly IGpuDevice _gd;
    readonly GpuRetireQueue _retired;
    readonly float _clipYSign;
    readonly WaterSettings _effective = new();

    IGpuShaderSet _fieldShaders = null!, _subtractShaders = null!, _sheetShaders = null!;
    IGpuResourceLayout _fieldLayout = null!, _subtractLayout = null!, _sheetLayout = null!;
    IGpuPipeline _fieldPipeline = null!, _subtractPipeline = null!, _sheetPipeline = null!;
    IGpuSampler _fieldSampler = null!;

    IGpuBuffer _uniformBuffer = null!;
    IGpuResourceSet _fieldSet = null!, _subtractSet = null!;
    IGpuResourceSet? _sheetSet;
    byte[] _image = Array.Empty<byte>();
    int[] _firstInstance = Array.Empty<int>(), _instanceCount = Array.Empty<int>();
    int _slots;

    IGpuBuffer? _instanceBuffer;
    Vector3[] _instances = Array.Empty<Vector3>();

    IGpuTexture? _field, _map;
    IGpuFramebuffer? _fieldFramebuffer, _mapFramebuffer;
    // The map the lit sets still name until the scene reports their replacement succeeded.
    IGpuTexture? _replacedMap;

    internal IGpuTexture? MapTexture => _map;
    internal IGpuTexture? FieldTexture => _field;
    internal int AtlasLayers { get; private set; }

    internal WaterCausticRenderer(IGpuDevice gd, GpuRetireQueue retired)
    {
        _gd = gd;
        _retired = retired;
        _clipYSign = GpuClip.Correct(Matrix4x4.Identity, gd.Capabilities).M22;
        try
        {
            CreatePrograms(gd.Factory);
            _fieldSampler = gd.Factory.CreateSampler(GpuSamplerDescription.Linear);
            EnsureSlots(InitialSlots);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void CreatePrograms(IGpuResourceFactory f)
    {
        _fieldLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(new GpuResourceLayoutElement(
            "CausticBody", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));
        _subtractLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(new GpuResourceLayoutElement(
            "CausticBody", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
        _sheetLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("CausticBody", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true),
            new GpuResourceLayoutElement("SurfaceField", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("SurfaceFieldSampler", GpuResourceKind.Sampler, GpuShaderStages.Vertex)));

        _fieldShaders = f.CreateShadersFromSpirv(ShaderSources.WaterSurfaceFieldVert, ShaderSources.WaterSurfaceFieldFrag);
        _subtractShaders = f.CreateShadersFromSpirv(ShaderSources.WaterCausticSubtractVert, ShaderSources.WaterCausticSubtractFrag);
        _sheetShaders = f.CreateShadersFromSpirv(ShaderSources.WaterCausticSheetVert, ShaderSources.WaterCausticSheetFrag);

        _fieldPipeline = CreatePipeline(f, _fieldShaders, _fieldLayout, GpuBlendAttachment.OverrideBlend, []);
        _subtractPipeline = CreatePipeline(f, _subtractShaders, _subtractLayout, PlusOne, []);
        _sheetPipeline = CreatePipeline(f, _sheetShaders, _sheetLayout, PlusOne,
        [
            new GpuVertexLayoutDescription(stride: 12, instanceStepRate: 1,
                elements: [new GpuVertexElement("Instance", GpuVertexElementFormat.Float3)]),
        ]);
    }

    static IGpuPipeline CreatePipeline(IGpuResourceFactory f, IGpuShaderSet shaders, IGpuResourceLayout layout,
        GpuBlendAttachment blend, List<GpuVertexLayoutDescription> vertexLayouts) =>
        f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = [blend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: true, scissorTestEnabled: true),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = vertexLayouts,
            Outputs = new GpuOutputDescription(null, AtlasFormat),
        });

    /// <summary>Grow both atlases to hold <paramref name="layers"/>, rounded up to 1, 2 or 4. Never shrinks.
    /// Returns true when the map texture changed. The replaced map stays alive until
    /// <see cref="RetireReplacedMap"/>, because the lit sets name it until their own replacement succeeds.</summary>
    internal bool EnsureLayers(int layers)
    {
        int capacity = layers <= 0 ? 0 : layers <= 2 ? layers : WaterCausticPlan.MaxLayers;
        if (capacity <= AtlasLayers) return false;

        IGpuResourceFactory f = _gd.Factory;
        IGpuTexture? field = null, map = null;
        IGpuFramebuffer? fieldFramebuffer = null, mapFramebuffer = null;
        IGpuResourceSet? sheetSet = null;
        try
        {
            field = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)capacity * FieldSide, FieldSide, AtlasFormat, AtlasUsage));
            map = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)capacity * MapSide, MapSide, AtlasFormat, AtlasUsage));
            fieldFramebuffer = f.CreateFramebuffer(null, field);
            mapFramebuffer = f.CreateFramebuffer(null, map);
            sheetSet = CreateSheetSet(_uniformBuffer, field);
        }
        catch
        {
            sheetSet?.Dispose();
            mapFramebuffer?.Dispose();
            fieldFramebuffer?.Dispose();
            map?.Dispose();
            field?.Dispose();
            throw;
        }

        _retired.Retire(_sheetSet, _fieldFramebuffer, _field);
        _retired.Retire(_mapFramebuffer);
        // A map whose replacement never reached the lit sets is named by nothing, so only the oldest needs holding.
        if (_replacedMap is null) _replacedMap = _map;
        else _retired.Retire(_map);

        _field = field;
        _map = map;
        _fieldFramebuffer = fieldFramebuffer;
        _mapFramebuffer = mapFramebuffer;
        _sheetSet = sheetSet;
        AtlasLayers = capacity;
        return true;
    }

    /// <summary>The lit sets name the current map now, so the one they named before can leave.</summary>
    internal void RetireReplacedMap()
    {
        _retired.Retire(_replacedMap);
        _replacedMap = null;
    }

    IGpuResourceSet CreateSheetSet(IGpuBuffer uniforms, IGpuTexture field) =>
        _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_sheetLayout,
            new GpuBufferRange(uniforms, 0, SlotBytes), field, _fieldSampler));

    void EnsureSlots(int bodies)
    {
        if (bodies <= _slots) return;
        int slots = Math.Max(bodies, _slots * 2);
        IGpuResourceFactory f = _gd.Factory;
        IGpuBuffer? buffer = null;
        IGpuResourceSet? fieldSet = null, subtractSet = null, sheetSet = null;
        try
        {
            buffer = f.CreateBuffer(new GpuBufferDescription((uint)slots * SlotBytes, GpuBufferUsage.UniformBuffer));
            var range = new GpuBufferRange(buffer, 0, SlotBytes);
            fieldSet = f.CreateResourceSet(new GpuResourceSetDescription(_fieldLayout, range));
            subtractSet = f.CreateResourceSet(new GpuResourceSetDescription(_subtractLayout, range));
            if (_field is not null) sheetSet = CreateSheetSet(buffer, _field);
        }
        catch
        {
            sheetSet?.Dispose();
            subtractSet?.Dispose();
            fieldSet?.Dispose();
            buffer?.Dispose();
            throw;
        }

        _retired.Retire(_fieldSet, _subtractSet, _sheetSet);
        _retired.Retire(_uniformBuffer);
        _uniformBuffer = buffer;
        _fieldSet = fieldSet;
        _subtractSet = subtractSet;
        _sheetSet = sheetSet;
        _image = new byte[slots * (int)SlotBytes];
        _firstInstance = new int[slots];
        _instanceCount = new int[slots];
        _slots = slots;
    }

    // Keep what is already written: a body reserves its worst case before writing behind the earlier bodies.
    void ReserveInstances(int count)
    {
        if (count > _instances.Length)
            Array.Resize(ref _instances, Math.Max(count, _instances.Length * 2));
    }

    void EnsureInstanceBuffer(int instances)
    {
        uint bytes = (uint)instances * 12u;
        if (_instanceBuffer is not null && _instanceBuffer.SizeInBytes >= bytes) return;
        uint capacity = Math.Max(bytes, (_instanceBuffer?.SizeInBytes ?? 0u) * 2u);
        IGpuBuffer buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(capacity, GpuBufferUsage.VertexBuffer));
        _retired.Retire(_instanceBuffer);
        _instanceBuffer = buffer;
    }

    public void Dispose()
    {
        _sheetSet?.Dispose();
        _subtractSet?.Dispose();
        _fieldSet?.Dispose();
        _mapFramebuffer?.Dispose();
        _fieldFramebuffer?.Dispose();
        _map?.Dispose();
        _replacedMap?.Dispose();
        _field?.Dispose();
        _instanceBuffer?.Dispose();
        _uniformBuffer?.Dispose();
        _fieldSampler?.Dispose();
        _sheetPipeline?.Dispose();
        _subtractPipeline?.Dispose();
        _fieldPipeline?.Dispose();
        _sheetShaders?.Dispose();
        _subtractShaders?.Dispose();
        _fieldShaders?.Dispose();
        _sheetLayout?.Dispose();
        _subtractLayout?.Dispose();
        _fieldLayout?.Dispose();
    }
}
