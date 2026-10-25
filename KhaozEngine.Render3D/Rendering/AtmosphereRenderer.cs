using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Diagnostics;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Fragment LUT producer recorded into the scene's command list before any atmosphere consumer.
/// Targets and consumer bindings remain stable across physical settings changes and enable toggles.</summary>
internal sealed class AtmosphereRenderer : IDisposable
{
    internal const uint UboBytes = 368;

    [StructLayout(LayoutKind.Sequential)]
    internal struct AtmosphereUbo
    {
        public Vector4 Planet;
        public Vector4 Rayleigh;
        public Vector4 Mie;
        public Vector4 Ozone;
        public Vector4 GroundHaze;
        public Vector4 Sun;
        public Vector4 Moon;
        public Vector4 Tint;
        public Vector4 Floors;
        public Vector4 Camera;
        public Vector4 Origin;
        public Vector4 SunT;
        public Vector4 MoonT;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Back;
        public Vector4 Ray;
        public Vector4 Res;
        public Vector4 Tone;
        public Vector4 NightTint;
        public Vector4 FogScatter;
        public Vector4 FogSource;
        public Vector4 FogHorizon;
    }

    readonly IGpuDevice _gd;
    readonly AtmosphereUpdatePolicy _policy = new();
    readonly IGpuResourceLayout _transmittanceLayout, _multiLayout, _skyLayout;
    readonly IGpuSampler _clampSampler;
    readonly IGpuFramebuffer _skyFramebuffer;
    readonly IGpuShaderSet _transmittanceShaders, _multiShaders, _skyShaders;
    readonly IGpuPipeline _transmittancePipeline, _multiPipeline, _skyPipeline;
    readonly IGpuResourceSet _transmittanceSet;
    IGpuFramebuffer? _transmittanceFramebuffer, _multiFramebuffer;
    IGpuResourceSet? _multiSet, _skySet;

    internal RenderPassProfiling? Profiling { get; set; }
    internal AtmosphereBindings Bindings { get; }
    internal IGpuTexture? TransmittanceTexture { get; private set; }
    internal IGpuTexture? MultiScatteringTexture { get; private set; }
    internal int StaticBuildCount { get; private set; }
    internal int SkyViewDrawCount { get; private set; }

    internal AtmosphereRenderer(IGpuDevice gd)
    {
        _gd = gd;
        var f = gd.Factory;
        Bindings = new AtmosphereBindings(gd);
        _skyFramebuffer = f.CreateFramebuffer(null, Bindings.SkyView);
        _clampSampler = f.CreateSampler(GpuSamplerDescription.Linear);
        _transmittanceLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(U("Atmosphere")));
        _multiLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            U("Atmosphere"), T("AtmoTransmittance"), S("AtmoClampSampler")));
        _skyLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            U("Atmosphere"), T("AtmoTransmittance"), T("AtmoMultiScattering"), S("AtmoClampSampler")));
        _transmittanceShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.AtmosphereTransmittanceFrag);
        _multiShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.AtmosphereMultiScatteringFrag);
        _skyShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.AtmosphereSkyViewFrag);
        _transmittancePipeline = Pipeline(_transmittanceShaders, _transmittanceLayout);
        _multiPipeline = Pipeline(_multiShaders, _multiLayout);
        _skyPipeline = Pipeline(_skyShaders, _skyLayout);
        _transmittanceSet = f.CreateResourceSet(new GpuResourceSetDescription(_transmittanceLayout, Bindings.Uniforms));
    }

    IGpuPipeline Pipeline(IGpuShaderSet shaders, IGpuResourceLayout layout) => _gd.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
    {
        BlendFactor = Vector4.Zero,
        BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
        DepthStencil = GpuDepthStencilState.Disabled,
        Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
            depthClipEnabled: false, scissorTestEnabled: false),
        Topology = GpuPrimitiveTopology.TriangleList,
        ResourceLayouts = new[] { layout },
        ShaderSet = shaders,
        VertexLayouts = new List<GpuVertexLayoutDescription>(),
        Outputs = _skyFramebuffer.Outputs,
    });

    void EnsureStaticTargets()
    {
        if (TransmittanceTexture is not null) return;
        var f = _gd.Factory;
        TransmittanceTexture = f.CreateTexture(GpuTextureDescription.Texture2D(256, 64,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        MultiScatteringTexture = f.CreateTexture(GpuTextureDescription.Texture2D(32, 32,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _transmittanceFramebuffer = f.CreateFramebuffer(null, TransmittanceTexture);
        _multiFramebuffer = f.CreateFramebuffer(null, MultiScatteringTexture);
        _multiSet = f.CreateResourceSet(new GpuResourceSetDescription(_multiLayout,
            Bindings.Uniforms, TransmittanceTexture, _clampSampler));
        _skySet = f.CreateResourceSet(new GpuResourceSetDescription(_skyLayout,
            Bindings.Uniforms, TransmittanceTexture, MultiScatteringTexture, _clampSampler));
    }

    internal void Record(IGpuCommandList cl, PixelPostProcessSettings post, Vector3 cameraRelative,
        Vector3 renderOrigin, Matrix4x4 view, Matrix4x4 projection, int width, int height)
    {
        if (!post.Atmosphere.Enabled)
        {
            if (Bindings.Enabled)
            {
                using var disabledPass = Profiling?.Pass(cl, "atmosphere.uniforms") ?? default;
                AtmosphereUbo disabled = default;
                cl.UpdateBuffer(Bindings.Uniforms, 0, in disabled);
                Bindings.Enabled = false;
            }
            else Profiling?.Pass(cl, "atmosphere.uniforms", executed: false);
            SkipLuts(cl);
            return;
        }
        using var cpu = Profiling?.Cpu("cpu.atmosphere.prepare") ?? default;
        var model = post.ResolveAtmosphereModel();
        var frame = ResolveFrame(post, model);
        float cameraHeightKm = post.Atmosphere.HeightKm(cameraRelative.Y + renderOrigin.Y);
        var lighting = model.EvaluateLighting(cameraHeightKm, frame);
        var scattering = AtmosphereFogScattering.Evaluate(post.Atmosphere, model, frame, cameraHeightKm,
            lighting.SunTransmittance, lighting.MoonTransmittance);
        var ubo = PackUbo(post.Atmosphere, frame, lighting, scattering, cameraRelative, renderOrigin, view, projection,
            width, height, post.Hdr);
        using (Profiling?.Pass(cl, "atmosphere.uniforms") ?? default) cl.UpdateBuffer(Bindings.Uniforms, 0, in ubo);
        Bindings.Enabled = true;
        EnsureStaticTargets();
        var decision = _policy.Decide(post.Atmosphere);
        using (Profiling?.Pass(cl, "atmosphere.transmittance", executed: decision.RebuildStatic) ?? default)
            if (decision.RebuildStatic) Draw(cl, _transmittanceFramebuffer!, _transmittancePipeline, _transmittanceSet);
        using (Profiling?.Pass(cl, "atmosphere.multiple-scattering", executed: decision.RebuildStatic) ?? default)
            if (decision.RebuildStatic) Draw(cl, _multiFramebuffer!, _multiPipeline, _multiSet!);
        if (decision.RebuildStatic) StaticBuildCount++;
        using (Profiling?.Pass(cl, "atmosphere.sky-view", executed: decision.DrawSkyView) ?? default)
            if (decision.DrawSkyView)
            {
                Draw(cl, _skyFramebuffer, _skyPipeline, _skySet!);
                SkyViewDrawCount++;
            }
    }

    void SkipLuts(IGpuCommandList cl)
    {
        Profiling?.Pass(cl, "atmosphere.transmittance", executed: false);
        Profiling?.Pass(cl, "atmosphere.multiple-scattering", executed: false);
        Profiling?.Pass(cl, "atmosphere.sky-view", executed: false);
    }

    static void Draw(IGpuCommandList cl, IGpuFramebuffer target, IGpuPipeline pipeline, IGpuResourceSet set)
    {
        cl.SetFramebuffer(target);
        cl.SetPipeline(pipeline);
        cl.SetGraphicsResourceSet(0, set);
        cl.Draw(3);
    }

    internal static AtmosphereFrame ResolveFrame(PixelPostProcessSettings post, AtmosphereModel model)
    {
        if (post.AtmosphereCycle is { } cycle) return model.CreateFrame(cycle);
        var sun = post.Sky.ResolveSunDirection(post.LightDirection);
        float elevation = MathF.Asin(Math.Clamp(sun.Y, -1f, 1f)) * (180f / MathF.PI);
        float t = Math.Clamp(elevation >= 0 ? elevation / 10f : -elevation / 12f, 0f, 1f);
        t = t * t * (3f - 2f * t);
        var weights = elevation >= 0 ? new Vector3(t, 1f - t, 0f) : new Vector3(0f, 1f - t, t);
        return new AtmosphereFrame(sun, -sun, post.Atmosphere.MoonIlluminanceFraction, model.Tint(weights));
    }

    internal static AtmosphereUbo PackUbo(AtmosphereSettings settings, in AtmosphereFrame frame,
        in AtmosphereLighting lighting, in AtmosphereFogScattering scattering, Vector3 cameraRelative,
        Vector3 renderOrigin, Matrix4x4 view, Matrix4x4 projection, int width, int height, HdrSettings hdr)
    {
        // FogHorizon holds the sine and cosine of the geometric horizon, constant per frame. It uses the camera
        // radius AtmosphereFog.Shade uses.
        float cameraHeightKm = MathF.Max(settings.HeightKm(cameraRelative.Y + renderOrigin.Y), 0f);
        var (horizonSin, horizonCos) = AtmosphereFogSampling.HorizonSinCos(
            settings.PlanetRadiusKm + (double)cameraHeightKm, settings.PlanetRadiusKm);
        return new()
        {
            Planet = new(settings.PlanetRadiusKm, settings.PlanetRadiusKm + settings.AtmosphereHeightKm,
                settings.WorldUnitsPerKilometre, settings.AerialDistanceScale),
            Rayleigh = new(settings.RayleighScatteringPerKm, settings.RayleighScaleHeightKm),
            Mie = new(settings.MieScatteringPerKm, settings.MieExtinctionPerKm, settings.MieScaleHeightKm, settings.MieAnisotropy),
            Ozone = new(settings.OzoneAbsorptionPerKm, settings.OzoneCenterKm),
            GroundHaze = new(settings.GroundAlbedo, settings.HazeDensityPerKm, settings.HazeScaleHeightKm, settings.OzoneWidthKm * .5f),
            Sun = new(Vector3.Normalize(frame.SunDirection), settings.SunIlluminance),
            Moon = new(Vector3.Normalize(frame.MoonDirection), frame.MoonFraction),
            Tint = new(frame.Tint, settings.Enabled ? 1f : 0f),
            Floors = new(settings.AmbientScale, settings.FillScale, settings.NightAmbientFloor, settings.NightSkyFloor),
            Camera = new(cameraRelative, settings.HeightKm(cameraRelative.Y + renderOrigin.Y)),
            Origin = new(renderOrigin, 0f),
            SunT = new(lighting.SunTransmittance, frame.SunOpacity),
            MoonT = new(lighting.MoonTransmittance, frame.MoonOpacity),
            // Row-vector view matrices store the world camera axes in their columns.
            Right = new(view.M11, view.M21, view.M31, 0f),
            Up = new(view.M12, view.M22, view.M32, 0f),
            Back = new(view.M13, view.M23, view.M33, 0f),
            Ray = new(1f / projection.M11, 1f / projection.M22,
                projection.M31 / projection.M11, projection.M32 / projection.M22),
            Res = new(width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f,
                SkyHorizonMath.IsPerspective(projection) ? 0f : 1f, settings.SunAngularRadiusRadians),
            Tone = new(hdr.Enabled ? 1f : 0f, MathF.Max(hdr.Exposure, 0f), (float)hdr.Operator,
                Math.Clamp(hdr.ChromaPreservation, 0f, 1f)),
            NightTint = new(settings.NightTint, 0f),
            FogScatter = new(scattering.RayleighScattering, scattering.MieScattering),
            FogSource = new(scattering.MultipleScattering, settings.AerialOnsetDistance),
            FogHorizon = new((float)horizonSin, (float)horizonCos, 0f, 0f),
        };
    }

    public void Dispose()
    {
        _skySet?.Dispose();
        _multiSet?.Dispose();
        _transmittanceSet.Dispose();
        _skyPipeline.Dispose();
        _multiPipeline.Dispose();
        _transmittancePipeline.Dispose();
        _skyShaders.Dispose();
        _multiShaders.Dispose();
        _transmittanceShaders.Dispose();
        _skyLayout.Dispose();
        _multiLayout.Dispose();
        _transmittanceLayout.Dispose();
        _clampSampler.Dispose();
        _skyFramebuffer.Dispose();
        _multiFramebuffer?.Dispose();
        _transmittanceFramebuffer?.Dispose();
        MultiScatteringTexture?.Dispose();
        TransmittanceTexture?.Dispose();
        Bindings.Dispose();
    }
}
