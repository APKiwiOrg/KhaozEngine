using System;

namespace KhaozEngine.Render3D;

/// <summary>Word-addressed coverage storage. No CLR structure is copied into a device buffer.</summary>
internal static class CoverageGpuLayout
{
    internal const int ModelWords = 16, ModelBytes = 64, MatrixWords = 16;
    internal const int HostHeaderWords = 64, SupportHeaderWords = 16, CommandWords = 80;
    internal const int ActorHeaderWords = 16, WorkHeaderWords = 64, FinalHeaderWords = 16;
    internal const int SourceAttributeWords = 20, SkinIndexWords = 4, RefinedVertexWords = 10;
    internal const int RefinedTriangleWords = 3, SourceTriangleWords = 8, RecipeWords = 7;
    internal const int MaxCommands = 4, MaxRegions = 32;

    internal static uint FrameWords(int boneCount) => Words(ActorHeaderWords + (long)boneCount * MatrixWords + MaxCommands * CommandWords + MaxCommands + MaxRegions);
    internal static uint SurfaceWords(CoverageGpuHostUpload host) => Words(FinalHeaderWords + ((long)host.SourceCount + host.VertexCount) * ModelWords);

    internal static uint WorkWords(CoverageGpuHostUpload h)
    {
        long events = Math.Max(Math.Max(h.SourceCount, h.AliasCount), Math.Max(Math.Max(h.PositionGroupCount, h.VertexCount),
            Math.Max(3L * h.TriangleCount, 2L * h.PositionGroupCount)));
        // Posed and flushed source, four refined vertex rows, one XYZ and three scalar group rows,
        // one face scalar row, two face-validation words and six words per event.
        return Words(WorkHeaderWords + 2L * h.SourceCount * ModelWords + 4L * h.VertexCount * ModelWords
            + 6L * h.PositionGroupCount + 3L * h.TriangleCount + 6L * events);
    }

    internal static CoverageGpuReservation CreateReservation(int actorSlot, uint staticBase, uint frameBase, uint workBase,
        uint currentBase, uint previousBase, CoverageGpuHostUpload host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfNegative(actorSlot);
        _ = Words(((long)actorSlot + 1) * FinalHeaderWords);
        End(staticBase, (uint)host.Words.Length); End(frameBase, FrameWords(host.BoneCount));
        End(workBase, WorkWords(host)); End(currentBase, SurfaceWords(host)); End(previousBase, SurfaceWords(host));
        return new(actorSlot, staticBase, frameBase, workBase, currentBase, previousBase, host.SourceCount, host.VertexCount, host.TriangleCount);
    }

    internal static uint Words(long count)
    {
        uint words = checked((uint)count);
        _ = checked(words * sizeof(uint));
        return words;
    }
    internal static uint Bytes(uint words) => checked(words * sizeof(uint));
    static void End(uint start, uint count) => _ = Bytes(checked(start + count));
}

internal readonly record struct CoverageGpuReservation(int ActorSlot, uint StaticBase, uint FrameBase, uint WorkBase,
    uint CurrentBase, uint PreviousBase, int SourceCount, int VertexCount, int TriangleCount);

/// <summary>Cold owned host words. Each table offset is relative to this host's 64-word header.</summary>
internal sealed record CoverageGpuHostUpload(uint[] Words, int SourceCount, int VertexCount, int TriangleCount,
    int PositionGroupCount, int AliasCount, int ShadingGroupCount, int BoneCount)
{
    internal uint SourceAttributes => Words[8];
    internal uint SkinIndices => Words[10];
    internal uint RefinedVertices => Words[12];
    internal uint RefinedTriangles => Words[14];
    internal uint SourceTriangles => Words[16];
    internal uint SourceAliases => Words[18];
    internal uint Indices => Words[20];
    internal uint PositionRecipes => Words[22];
    internal uint GroupWeights => Words[24];
    internal uint VertexWeights => Words[26];
    internal uint EdgeParameters => Words[28];
    internal uint GroupRepresentatives => Words[30];
    internal uint CoreOffsets => Words[32];
    internal uint CoreFaces => Words[34];
    internal uint VertexOffsets => Words[36];
    internal uint VertexCorners => Words[38];
    internal uint ShadingOffsets => Words[40];
    internal uint ShadingCorners => Words[42];
    internal uint ActiveCandidateOffsets => Words[44];
    internal uint ActiveCandidates => Words[46];
    internal uint RefinedPullIndices => Words[48];
    internal uint FallbackPullIndices => Words[50];
    internal uint ByteCount => CoverageGpuLayout.Bytes((uint)Words.Length);
}

/// <summary>Cold owned support words. Relative offsets preserve the Task 3 support query ABI on relocation.</summary>
internal sealed record CoverageGpuSupportUpload(uint[] Words, int TriangleCount, int BoundaryCount)
{
    internal uint Positions => Words[7];
    internal uint SourceIndices => Words[8];
    internal uint Triangles => Words[9];
    internal uint BoundaryVertices => Words[10];
    internal uint CellOffsets => Words[11];
    internal uint CellTriangles => Words[12];
    internal uint ByteCount => CoverageGpuLayout.Bytes((uint)Words.Length);
}
