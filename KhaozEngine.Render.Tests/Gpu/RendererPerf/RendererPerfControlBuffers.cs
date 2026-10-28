using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// One definition owns allocation sizes and admission. Transfer totals are not per-binding requirements.
internal readonly record struct RendererPerfControlBuffers(uint InputBytes, uint OutputBytes, int OutputCount)
{
    internal static RendererPerfControlBuffers For(RendererPerfControlFacts facts) => facts.Id switch
    {
        1 or 2 or 3 or 11 or 12 => default,
        4 => new(4, 4, 1),
        5 or 6 => new(4, 4, 36),
        7 or 8 => new(checked((uint)facts.ReadBytes), checked((uint)facts.WriteBytes), 1),
        9 or 10 => new(RendererPerfControls.ArithmeticInvocations * 4u, checked((uint)facts.WriteBytes), 1),
        _ => throw new ArgumentOutOfRangeException(nameof(facts))
    };

    internal void Admit(GpuCapabilities capabilities, int controlId)
    {
        uint required = Math.Max(InputBytes, OutputBytes);
        if (required == 0) return;
        uint limit = capabilities.MaxStorageBufferRangeBytes;
        if (!capabilities.SupportsCompute || required > limit)
        {
            string reason = !capabilities.SupportsCompute ? "Compute is unsupported."
                : limit == 0 ? "The device did not report a storage-buffer range."
                : "A whole storage-buffer binding exceeds the admitted range.";
            throw new RendererPerfProtocolException("Compatibility",
                $"Control {controlId} requires {required} bytes per storage-buffer binding, device limit {limit} bytes. {reason}");
        }
    }
}
