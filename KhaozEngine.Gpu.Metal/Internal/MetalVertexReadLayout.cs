using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Pipeline-owned CPU read metadata. Attribute sizes and stream membership never change per draw.</summary>
internal sealed class MetalVertexReadLayout
{
    internal readonly record struct Attribute(ulong Offset, ulong Bytes);
    internal readonly record struct Stream(uint Stride, uint StepRate, bool PerInstance, Attribute[] Attributes);
    internal readonly Stream[] Streams;

    internal MetalVertexReadLayout(MetalVertexStream[] streams, MetalVertexAttribute[] attributes)
    {
        Streams = new Stream[streams.Length];
        for (int slot = 0; slot < streams.Length; slot++)
        {
            var stream = streams[slot];
            int count = 0;
            foreach (var attribute in attributes) if (attribute.BufferIndex == stream.BufferIndex) count++;
            var reads = new Attribute[count];
            int next = 0;
            foreach (var attribute in attributes)
                if (attribute.BufferIndex == stream.BufferIndex)
                    reads[next++] = new(attribute.OffsetBytes, MetalFormats.VertexElementSize(attribute.Format));
            Streams[slot] = new(stream.Stride, stream.StepRate,
                stream.StepFunction == MTLVertexStepFunction.PerInstance, reads);
        }
    }
}
