using System;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Managed validation of the native compiler's returned evidence.</summary>
internal static class D3D11ShaderResultValidation
{
    internal static void RequireFxcOutput(int byteLength, string profile, string label)
    {
        if (byteLength == 0)
        {
            throw new ShaderValidationException(ShaderValidationRefusal.D3D11FxcOutputEmpty,
                $"{label}: FXC reported success for profile {profile} and produced no bytes. Nothing "
                + "downstream can use an empty module, and a zero-length DXBC handed to the device fails "
                + "somewhere far less informative than here.")
            {
                Label = label,
                Profile = profile,
            };
        }
    }

    internal static void RequireVertexModule(int byteLength, string label)
    {
        if (byteLength == 0)
        {
            throw new ShaderValidationException(ShaderValidationRefusal.D3D11VertexModuleEmpty,
                $"{label}: cannot reflect an empty vertex module. The input signature is what the contiguity "
                + "check reads, so there is nothing to check and nothing to build an input layout against.")
            {
                Label = label,
            };
        }
    }

    internal static void ThrowVertexReflectionFailure(Exception ex, string label)
    {
        throw new ShaderValidationException(ShaderValidationRefusal.D3D11VertexReflectionFailed,
            $"{label}: the compiled vertex module could not be reflected, so its input signature cannot "
            + $"be checked for the holed-TEXCOORD hazard: {ex.Message}", ex)
        {
            Label = label,
            Diagnostic = ex.Message,
        };
    }
}
