using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FoliageInstanceData
    {
        public Matrix4x4 Model;
        public Vector4 Parameters; // rank, local root Y, inverse local height, alpha cutoff
        public const uint SizeInBytes = 80;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FoliageUniforms
    {
        public Vector4 FocusRadius, Density, FadeWind, WindTime;
        public Vector4 Interactor0, Interactor1, Interactor2, Interactor3, Strengths;
        public const uint SizeInBytes = 144;
        public const uint SlotBytes = 256;

        public static FoliageUniforms Build(Vector3 focus, in FoliageRenderSettings settings,
            ReadOnlySpan<FoliageInteractor> interactors, float time)
        {
            float radius = MathF.Min(settings.DensityRadius ?? settings.DrawRadius, settings.DrawRadius);
            Vector2 direction = settings.WindDirection;
            if (direction.LengthSquared() > .000001f) direction = Vector2.Normalize(direction);
            var data = new FoliageUniforms
            {
                FocusRadius = new Vector4(focus, settings.DrawRadius),
                Density = new Vector4(settings.QualityDensity, MathF.Min(settings.DistantDensity, settings.QualityDensity),
                    radius, MathF.Min(settings.FadeBandWidth, radius)),
                FadeWind = new Vector4(settings.InstanceFadeBandWidth, settings.WindStrength,
                    settings.WindSpeed, settings.WindSpatialFrequency),
                WindTime = new Vector4(direction, float.IsFinite(time) ? time : 0f, settings.FadeBandWidth),
            };
            Span<Vector4> locations = MemoryMarshal.CreateSpan(ref data.Interactor0, 4);
            Span<float> strengths = MemoryMarshal.CreateSpan(ref data.Strengths.X, 4);
            for (int i = 0; i < interactors.Length; i++)
            {
                locations[i] = new Vector4(interactors[i].Position, interactors[i].Radius);
                strengths[i] = interactors[i].Strength;
            }
            return data;
        }
    }

    IGpuPipeline? _foliagePipeline;
    IGpuShaderSet? _foliageShaders;
    IGpuResourceLayout? _foliageLayout;
    IGpuResourceSet? _foliageSet;
    IGpuBuffer? _foliageUbo;
    GpuOutputDescription _foliageOutputs;
    byte[] _foliageImage = Array.Empty<byte>();
    uint _foliageSlots;

    void SetFoliageOutputs(GpuOutputDescription outputs)
    {
        _foliageOutputs = outputs;
        if (_foliagePipeline is null) return;
        _foliagePipeline.Dispose();
        _foliagePipeline = null;
        EnsureFoliagePipeline();
    }

    void EnsureFoliagePipeline()
    {
        if (_foliagePipeline is not null) return;
        var factory = _gd.Factory;
        _foliageLayout ??= factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Foliage", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
        _foliageShaders ??= factory.CreateShadersFromSpirv(ShaderSources.FoliageVert, ShaderSources.ModelFrag);
        _foliagePipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: true, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _layout, _foliageLayout },
            ShaderSet = _foliageShaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>
            {
                new GpuVertexLayoutDescription(
                    new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                    new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                    new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                    new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4)),
                new GpuVertexLayoutDescription(FoliageInstanceData.SizeInBytes, 1, new[]
                {
                    new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("FoliageParameters", GpuVertexElementFormat.Float4),
                }),
            },
            Outputs = _foliageOutputs,
        });
    }

    public long UploadFoliageUniforms(IGpuCommandList cl, ReadOnlySpan<FoliageUniforms> uniforms)
    {
        if (uniforms.Length == 0) return 0;
        EnsureFoliagePipeline();
        if (_foliageUbo is null || _foliageSlots < uniforms.Length)
        {
            if (_foliageSet is not null) _retired.Add(_foliageSet);
            if (_foliageUbo is not null) _retired.Add(_foliageUbo);
            _foliageSlots = Math.Max((uint)uniforms.Length, _foliageSlots == 0 ? 4u : _foliageSlots * 2);
            _foliageImage = new byte[checked((int)(_foliageSlots * FoliageUniforms.SlotBytes))];
            _foliageUbo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)_foliageImage.Length, GpuBufferUsage.UniformBuffer));
            _foliageSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_foliageLayout!,
                new GpuBufferRange(_foliageUbo, 0, FoliageUniforms.SlotBytes)));
        }
        for (int i = 0; i < uniforms.Length; i++)
            MemoryMarshal.Write(_foliageImage.AsSpan(i * (int)FoliageUniforms.SlotBytes), in uniforms[i]);
        // A whole-buffer update avoids the partial uniform upload path on native D3D11.
        cl.UpdateBuffer<byte>(_foliageUbo, 0, _foliageImage);
        return _foliageImage.Length;
    }

    public void DrawFoliageMesh(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat indexFormat, IGpuBuffer instances, uint start, uint count, uint uniformSlot,
        IGpuResourceSet? materialSet)
    {
        cl.SetPipeline(_foliagePipeline!);
        cl.SetGraphicsResourceSet(0, materialSet ?? _defaultSet);
        cl.SetGraphicsResourceSet(1, _foliageSet!, uniformSlot * FoliageUniforms.SlotBytes);
        cl.SetVertexBuffer(0, vb);
        cl.SetVertexBuffer(1, instances);
        cl.SetIndexBuffer(ib, indexFormat);
        cl.DrawIndexed((uint)indexCount, count, 0, 0, start);
    }

    void DisposeFoliageResources()
    {
        _foliagePipeline?.Dispose();
        _foliageShaders?.Dispose();
        _foliageSet?.Dispose();
        _foliageUbo?.Dispose();
        _foliageLayout?.Dispose();
    }
}
