using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The frame's indirect-light tail, appended after the interior-cut uniforms.</summary>
internal readonly struct IndirectUniforms
{
    internal const uint TailOffset = ModelRenderer.InteriorCutWindowOffset + ModelRenderer.InteriorCutBytes;
    internal const uint TailBytes = (1 + IrradianceSH.CoefficientCount) * 16;

    readonly bool _directional;
    readonly IrradianceSH _irradiance;
    readonly ShaderGateProbe _gates;

    internal IndirectUniforms(PixelPostProcessSettings settings, ShaderGateProbe gates = ShaderGateProbe.None)
    {
        _gates = gates;
        _directional = settings.IndirectDiffuse == IndirectDiffuseMode.Directional;
        _irradiance = settings.AmbientIrradiance;
        if (_directional && !settings.Atmosphere.Enabled && _irradiance == default)
            _irradiance = IrradianceSH.Uniform(new Vector3(
                settings.AmbientColor.R, settings.AmbientColor.G, settings.AmbientColor.B));
    }

    internal void WriteTo(Span<byte> frameImage)
    {
        Span<Vector4> tail = MemoryMarshal.Cast<byte, Vector4>(frameImage.Slice((int)TailOffset, (int)TailBytes));
        // y and z are the private probe's fragment switches, zero outside a sampling window.
        float pointShadow = (_gates & ShaderGateProbe.PointShadowZeroNdl) != 0 ? 1 : 0;
        float underwater = (_gates & ShaderGateProbe.UnderwaterMarchZero) != 0 ? 1 : 0;
        if (!_directional)
        {
            tail.Clear();
            tail[0] = new Vector4(0, pointShadow, underwater, 0);
            return;
        }

        tail[0] = new Vector4(1, pointShadow, underwater, 0);
        for (int i = 0; i < IrradianceSH.CoefficientCount; i++)
            tail[i + 1] = new Vector4(_irradiance.Coefficient(i), 0);
    }
}
