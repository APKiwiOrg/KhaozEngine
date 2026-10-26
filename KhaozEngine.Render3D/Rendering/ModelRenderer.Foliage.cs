using System;
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
        public Vector4 Motion; // interactor bend scale, maximum lean, wind scale, shiver
        public const uint SizeInBytes = 96;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FoliageUniforms
    {
        public Vector4 FocusRadius, Density, FadeWind, WindTime;
        public Vector4 Interactor0, Interactor1, Interactor2, Interactor3, Strengths;
        // x = blade pixels where wind stops, y = metres per internal pixel at clip w of 1, z = the same last frame
        public Vector4 WindFade;
        public Vector4 Agitation;
        // Last frame's state for FoliageMotionVert, zero while temporal rendering is off. Both programs declare
        // the complete 288-byte block. Dynamic offsets require 256-byte alignment (D3D11UniformRing), so slots are 512.
        public Vector4 PrevFocus;   // xyz = last frame's focus, w = last frame's wind time
        public Vector4 PrevInteractor0, PrevInteractor1, PrevInteractor2, PrevInteractor3, PrevStrengths;
        public Vector4 PrevAgitation;
        public const uint SizeInBytes = 288;
        public const uint SlotBytes = 512;

        /// <summary>Metres one internal pixel covers vertically at clip w of 1. Perspective clip w is view depth.
        /// Orthographic clip w is 1, which gives view height over render height. Zero when degenerate.</summary>
        public static float MetresPerPixel(in Matrix4x4 projection, int renderHeight)
        {
            float pixelsPerUnit = MathF.Abs(projection.M22) * renderHeight;
            float scale = pixelsPerUnit > 0f ? 2f / pixelsPerUnit : 0f;
            return float.IsFinite(scale) ? scale : 0f;
        }

        /// <summary>Writes the frame's pixel scale into every slot. Submission can precede the final camera
        /// and render size, so the scale is applied when the slots upload.</summary>
        public static void ApplyPixelScale(Span<FoliageUniforms> uniforms, float metresPerPixel)
        {
            foreach (ref FoliageUniforms slot in uniforms) slot.WindFade.Y = metresPerPixel;
        }

        /// <summary>As the one-argument overload, plus last frame's scale in <c>WindFade.z</c> for the temporal
        /// variant's previous wind fade.</summary>
        public static void ApplyPixelScale(Span<FoliageUniforms> uniforms, float metresPerPixel, float previousMetresPerPixel)
        {
            foreach (ref FoliageUniforms slot in uniforms)
            {
                slot.WindFade.Y = metresPerPixel;
                slot.WindFade.Z = previousMetresPerPixel;
            }
        }

        /// <summary>This submission with <paramref name="previous"/>'s focus, clock, interactors, strengths and agitation as
        /// last frame's.</summary>
        public readonly FoliageUniforms WithPrevious(in FoliageUniforms previous)
        {
            FoliageUniforms data = this;
            data.PrevFocus = new Vector4(previous.FocusRadius.X, previous.FocusRadius.Y, previous.FocusRadius.Z,
                previous.WindTime.Z);
            data.PrevInteractor0 = previous.Interactor0;
            data.PrevInteractor1 = previous.Interactor1;
            data.PrevInteractor2 = previous.Interactor2;
            data.PrevInteractor3 = previous.Interactor3;
            data.PrevStrengths = previous.Strengths;
            data.PrevAgitation = previous.Agitation;
            return data;
        }

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
                WindFade = new Vector4(settings.WindFadeBladePixels, 0f, 0f, 0f),
            };
            Span<Vector4> locations = MemoryMarshal.CreateSpan(ref data.Interactor0, 4);
            Span<float> strengths = MemoryMarshal.CreateSpan(ref data.Strengths.X, 4);
            Span<float> agitation = MemoryMarshal.CreateSpan(ref data.Agitation.X, 4);
            for (int i = 0; i < interactors.Length; i++)
            {
                locations[i] = new Vector4(interactors[i].Position, interactors[i].Radius);
                strengths[i] = interactors[i].Strength;
                agitation[i] = interactors[i].Agitation;
            }
            return data;
        }
    }

    internal IGpuResourceLayout FoliageLayout => _foliageLayout!;
    internal IGpuResourceSet FoliageSet => _foliageSet!;

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
        _retired.Retire(_foliagePipeline);
        _foliagePipeline = null;
        EnsureFoliagePipeline();
    }

    void EnsureFoliagePipeline()
    {
        if (_foliagePipeline is not null) return;
        var factory = _gd.Factory;
        _foliageLayout ??= factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Foliage", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
        // The temporal variant against a target with the motion attachment, the base program otherwise.
        ModelMotionResources? motion = MotionMath.IsTemporal(_foliageOutputs) ? _motion : null;
        _foliagePipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = ModelTargetBlends.Opaque(_foliageOutputs),
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: true, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = motion is null
                ? new[] { _layout, _foliageLayout }
                : new[] { _layout, _foliageLayout, motion.FrameLayout },
            ShaderSet = _visibilityShaders.TrySelect(factory, _foliageOutputs, motion is null ? ShaderSources.FoliageVert : ShaderSources.FoliageMotionVert,
                motion is null ? ShaderSources.ModelFrag : ShaderSources.ModelMotionFrag, preserve: false, out IGpuShaderSet visibilityShaders)
                ? visibilityShaders : motion?.FoliageShaders
                    ?? (_foliageShaders ??= factory.CreateShadersFromSpirv(ShaderSources.FoliageVert, ShaderSources.ModelFrag)),
            VertexLayouts = new(FoliageVertexLayouts.Create()),
            Outputs = _foliageOutputs,
        });
    }

    public long UploadFoliageUniforms(IGpuCommandList cl, ReadOnlySpan<FoliageUniforms> uniforms)
    {
        if (uniforms.Length == 0) return 0;
        EnsureFoliagePipeline();
        if (_foliageUbo is null || _foliageSlots < uniforms.Length)
        {
            if (_foliageSet is not null) _retired.Retire(_foliageSet);
            if (_foliageUbo is not null) _retired.Retire(_foliageUbo);
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
        if (_motion is { } motion) cl.SetGraphicsResourceSet(2, motion.FrameSet);   // the foliage variant's motion block
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
