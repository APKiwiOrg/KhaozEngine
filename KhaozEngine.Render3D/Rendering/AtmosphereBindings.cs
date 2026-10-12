using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Stable scene-owned atmosphere resources appended after each fragment consumer's set 0 prefix.
/// Standalone consumers can own an instance instead. Construction always initializes a disabled UBO.</summary>
internal sealed class AtmosphereBindings : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FogBlendUniforms { public Matrix4x4 ViewProj; public Vector4 FogBlend; }

    internal IGpuBuffer Uniforms { get; }
    internal IGpuTexture SkyView { get; }
    internal IGpuSampler SkySampler { get; }
    /// <summary>Enabled flag in the most recently recorded UBO. Disabled effects share their alpha matrix lane.</summary>
    internal bool Enabled { get; set; }

    internal FogBlendUniforms PackFogBlendFrame(Matrix4x4 viewProj, bool additive = false) => new()
    {
        ViewProj = viewProj,
        FogBlend = new(additive ? 1f : 0f, Enabled ? 1f : 0f, 0f, 0f),
    };

    internal AtmosphereBindings(IGpuDevice gd)
    {
        var f = gd.Factory;
        Uniforms = f.CreateBuffer(new GpuBufferDescription(AtmosphereRenderer.UboBytes, GpuBufferUsage.UniformBuffer));
        AtmosphereRenderer.AtmosphereUbo disabled = default;
        gd.UpdateBuffer(Uniforms, 0, in disabled);
        SkyView = f.CreateTexture(GpuTextureDescription.Texture2D(192, 108, GpuPixelFormat.R16G16B16A16Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        SkySampler = f.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.MinLinearMagLinearMipLinear,
            GpuSamplerAddress.Wrap, GpuSamplerAddress.Clamp));
    }

    internal static GpuResourceLayoutElement[] AppendLayout(params GpuResourceLayoutElement[] existing)
    {
        var result = new GpuResourceLayoutElement[existing.Length + 3];
        existing.CopyTo(result, 0);
        result[existing.Length] = U("Atmosphere");
        result[existing.Length + 1] = T("AtmoSkyView");
        result[existing.Length + 2] = S("AtmoSkySampler");
        return result;
    }

    internal IGpuBindableResource[] AppendResources(params IGpuBindableResource[] existing)
    {
        var result = new IGpuBindableResource[existing.Length + 3];
        existing.CopyTo(result, 0);
        result[existing.Length] = Uniforms;
        result[existing.Length + 1] = SkyView;
        result[existing.Length + 2] = SkySampler;
        return result;
    }

    public void Dispose()
    {
        SkySampler.Dispose();
        SkyView.Dispose();
        Uniforms.Dispose();
    }
}
