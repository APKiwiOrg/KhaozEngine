using System.Runtime.CompilerServices;
using System.Threading;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The independent R8 key-shadow signal carried by the lit framebuffer.</summary>
internal static class ShadowVisibilityTargets
{
    internal const GpuPixelFormat Format = GpuPixelFormat.R8UNorm;
    static readonly ConditionalWeakTable<IGpuDevice, CapabilityReport> Reports = new();
    sealed class CapabilityReport { internal int Logged; }

    internal static int Attachment(bool motion) => motion ? 4 : 3;

    internal static bool HasVisibility(in GpuOutputDescription outputs)
    {
        int slot = Attachment(MotionMath.IsTemporal(outputs));
        return outputs.Colour.Length == slot + 1 && outputs.Colour[slot] == Format;
    }

    /// <summary>Choose the approved degraded layout before creating any target or pipeline.</summary>
    internal static bool Requested(bool requested, IGpuDevice device)
    {
        if (!requested) return false;
        if (device.Capabilities.MaxColorAttachments >= 5) return true;
        CapabilityReport report = Reports.GetValue(device, static _ => new CapabilityReport());
        if (Interlocked.Exchange(ref report.Logged, 1) == 0)
            Log.For<RenderResources>().Info($"GPU capability: {device.Capabilities.MaxColorAttachments} color attachments. "
                + "Temporal shadow visibility is unavailable. The bilinear shadow filter remains active.");
        return false;
    }

    /// <summary>Export the already sampled signal, or a harmless output discarded by destination-preserving blending.</summary>
    internal static string Fragment(string program, bool motion, bool preserve = false)
    {
        program = ShaderText.After(program, "layout(location=2) out vec4 oDepth;",
            $"\nlayout(location={Attachment(motion)}) out float oKeyVisibility;");
        return preserve
            ? ShaderText.BeforeEndOfMain(program, "    oKeyVisibility = 0.0;\n")
            : ShaderText.After(program,
                "float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);",
                "\n    oKeyVisibility = keyShadow;");
    }
}
