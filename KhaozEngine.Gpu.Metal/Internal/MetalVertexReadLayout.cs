using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Pipeline-owned CPU read metadata. Attribute sizes and stream membership never change per draw.</summary>
internal sealed class MetalVertexReadLayout
{
    internal readonly record struct Attribute(ulong Offset, ulong Bytes);
    // Equivalent pipeline layouts share coverage without interning or retaining a global pipeline cache.
    internal readonly struct Stream : IEquatable<Stream>
    {
        internal readonly uint Stride, StepRate;
        internal readonly bool PerInstance;
        internal readonly Attribute[] Attributes, WindowAttributes;
        readonly int _hash;

        internal Stream(uint stride, uint stepRate, bool perInstance, Attribute[] attributes)
        {
            Stride = stride;
            StepRate = stepRate;
            PerInstance = perInstance;
            Attributes = attributes;
            WindowAttributes = WindowRanges(attributes);
            var hash = new HashCode();
            hash.Add(stride);
            hash.Add(stepRate);
            hash.Add(perInstance);
            foreach (var attribute in attributes) hash.Add(attribute);
            _hash = hash.ToHashCode();
        }

        static Attribute[] WindowRanges(Attribute[] attributes)
        {
            var ranges = new Attribute[attributes.Length];
            int count = 0;
            foreach (var attribute in attributes)
            {
                if (count != 0 && ranges[count - 1].Offset + ranges[count - 1].Bytes == attribute.Offset)
                {
                    var previous = ranges[count - 1];
                    ranges[count - 1] = new(previous.Offset, previous.Bytes + attribute.Bytes);
                }
                else ranges[count++] = attribute;
            }
            Array.Resize(ref ranges, count);
            return ranges;
        }

        public bool Equals(Stream other) => Stride == other.Stride && StepRate == other.StepRate
            && PerInstance == other.PerInstance && Attributes.AsSpan().SequenceEqual(other.Attributes);
        public override bool Equals(object? obj) => obj is Stream other && Equals(other);
        public override int GetHashCode() => _hash;
    }
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
