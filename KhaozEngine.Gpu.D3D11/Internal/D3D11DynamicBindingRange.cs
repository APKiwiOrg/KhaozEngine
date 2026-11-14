namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// Constant-buffer window admission shared by immediate binding, headless tracing and deferred encoding.
    /// Refuse before recording a bind, so invalid windows never reach replay or replace a pending valid set.
    /// </summary>
    internal static class D3D11DynamicBindingRange
    {
        internal static void RequireValid(uint slot, D3D11ResourceSet set, uint dynamicOffset)
        {
            var bindings = set.Bindings;
            for (int i = 0; i < bindings.Length; i++)
            {
                ref readonly D3D11BoundResource binding = ref bindings[i];
                if (binding.Kind != GpuResourceKind.UniformBuffer) continue;

                uint applied = binding.Dynamic ? dynamicOffset : 0;
                D3D11UniformRing? ring = (binding.Buffer as ID3D11RingBacked)?.Ring;
                uint? stride = ring?.SegmentStrideBytes;

                // Match Metal and Vulkan's refusal order. Widen before adding so uint wrap cannot hide an overrun.
                if (stride is { } segmentStride && (ulong)binding.OffsetBytes + applied + binding.SizeBytes > segmentStride)
                {
                    throw new GpuDynamicBindingRangeException(GpuDynamicBindingRefusal.WindowOutsideSegment,
                        "range", binding.SizeBytes,
                        "A native Direct3D 11 uniform bind runs past its own ring segment. Bind a narrower "
                        + "GpuBufferRange window to leave room for the per-draw offset.")
                    {
                        Set = slot,
                        Binding = (uint)i,
                        RangeOffset = binding.OffsetBytes,
                        DynamicOffset = applied,
                        RangeBytes = binding.SizeBytes,
                        SegmentStride = segmentStride,
                        RecoveryOperation = "GpuBufferRange",
                    };
                }

                ulong frameBase = ring?.CurrentFrameBaseBytes ?? 0;
                ulong composed = frameBase + binding.OffsetBytes + applied;
                // D3D11 requires 16 constants of 16 bytes, independently of any device alignment query.
                if (composed % D3D11ConstantRange.OffsetAlignmentBytes == 0) continue;

                throw new GpuDynamicBindingRangeException(GpuDynamicBindingRefusal.OffsetMisaligned,
                    nameof(dynamicOffset), applied,
                    "A native Direct3D 11 constant-buffer offset is not a multiple of the required 256-byte "
                    + "boundary. Align the composed range and per-draw offsets before binding.")
                {
                    Set = slot,
                    Binding = (uint)i,
                    ComposedOffset = composed,
                    AlignmentBytes = D3D11ConstantRange.OffsetAlignmentBytes,
                    RingBase = frameBase,
                    RangeOffset = binding.OffsetBytes,
                    DynamicOffset = applied,
                    RangeBytes = binding.SizeBytes,
                    SegmentStride = stride,
                };
            }
        }
    }
}
