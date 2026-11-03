using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalCommandList
{
    readonly Dictionary<uint, (MetalBuffer Buffer, uint Offset)> _readStreams = [];
    MetalBuffer? _readIndices;

    void RecordGraphicsReads(MetalGraphicsPipeline pipeline, uint vertexStart, uint vertexCount,
        uint instanceStart, uint instanceCount, bool indexed)
    {
        if (_prefixFrame != 0 || vertexCount == 0 || instanceCount == 0) return;
        _graphicsBinds.RecordReads(_recordedReads, _segment);
        for (uint slot = 0; slot < pipeline.Plan.Streams.Length; slot++)
        {
            if (!_readStreams.TryGetValue(slot, out var binding) || binding.Buffer.Handle.IsNull) continue;
            var stream = pipeline.Plan.Streams[slot];
            bool perInstance = stream.StepFunction == MTLVertexStepFunction.PerInstance;
            // GPU-produced indices can address every vertex reachable through the binding. Instance and
            // non-indexed windows are draw-bounded. Attribute padding stays writable in either case.
            ulong first = perInstance ? instanceStart / stream.StepRate : vertexStart;
            ulong last = perInstance ? ((ulong)instanceStart + instanceCount - 1) / stream.StepRate
                : (ulong)vertexStart + vertexCount - 1;
            foreach (var attribute in pipeline.Plan.Attributes)
            {
                if (attribute.BufferIndex != stream.BufferIndex) continue;
                ulong bytes = MetalFormats.VertexElementSize(attribute.Format);
                ulong offset = (ulong)binding.Offset + attribute.OffsetBytes;
                if (indexed && !perInstance)
                {
                    first = 0;
                    if (offset + bytes > binding.Buffer.SizeInBytes || stream.Stride == 0) continue;
                    last = (binding.Buffer.SizeInBytes - offset - bytes) / stream.Stride;
                }
                _recordedReads.AddStrided(binding.Buffer.CpuResource, offset + first * stream.Stride,
                    bytes, stream.Stride, last - first + 1);
            }
        }
    }

    void RecordStagingAccess(MetalTexture source, uint level, uint layer, uint width, uint height)
    {
        if (_prefixFrame != 0 || !source.IsStaging) return;
        var layout = source.SubresourceLayout(level, layer);
        _recordedReads.AddStrided(source.CpuResource, layout.Offset,
            MetalStagingLayout.RowPitch(width, source.Format), layout.RowPitch, height);
    }
}
