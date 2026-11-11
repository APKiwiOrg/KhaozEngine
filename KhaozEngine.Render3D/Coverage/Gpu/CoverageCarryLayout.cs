using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Word offsets shared by the carry stages. Surface bases are uint words, not vertices or bytes.</summary>
internal static class CoverageCarryLayout
{
    internal const int ModelWords = 16, HeaderWords = 64, FrameHeaderWords = 16, CommandWords = 8;
    internal const int MaxRegions = 32, ResultWords = 8;
    internal static int Round64(int extent) => checked((extent + 63) / 64 * 64);
    internal static int FrameWords(int bones) => checked(FrameHeaderWords + bones * 16 + 4 * CommandWords + MaxRegions);
    internal static uint Bytes(long words) => checked((uint)(words * 4));
    internal static int WorkWords(int actors, int source, int vertices, int triangles)
        => checked(actors * HeaderWords + source * 16 + vertices * 49 + triangles * 46);

    internal static uint[] WorkHeader(CoverageRefinement topology)
    {
        uint[] header = new uint[HeaderWords];
        uint cursor = HeaderWords;
        Row(0, topology.SourceVertexCount, 16); header[2] = header[0]; header[3] = header[1];
        Row(4, topology.VertexCount, 16); Row(8, topology.VertexCount, 16); Row(10, topology.VertexCount, 16);
        Row(42, topology.TriangleCount, 46); Row(44, topology.VertexCount, 1);
        return header;
        void Row(int pair, int count, int stride)
        { header[pair] = cursor; header[pair + 1] = (uint)count; cursor = checked(cursor + (uint)count * (uint)stride); }
    }

    internal static GpuResourceLayoutDescription Resources(int stage)
    {
        GpuResourceLayoutElement Element(string name, bool readOnly) => new(name,
            readOnly ? GpuResourceKind.StructuredBufferReadOnly : GpuResourceKind.StructuredBufferReadWrite,
            GpuShaderStages.Compute);
        GpuResourceLayoutElement[] common = [Element("CovStaticBuf", true), Element("CovFrameBuf", true),
            Element("CovWorkBuf", false), Element("CovResultsBuf", false)];
        return stage switch
        {
            1 => new([.. common, Element("CarryTableBuf", true)]),
            4 => new([.. common, Element("CovCurrentBuf", false)]),
            _ => new(common),
        };
    }
}
