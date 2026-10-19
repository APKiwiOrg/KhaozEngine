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
    internal const int FailureCode = 26, FailureSlot = 27, FailureCount = 28, FailureStage = 29, Refused = 30, BeforePublished = 31;
    internal const int AttributeCandidates = 32;
    internal const int ConformContinuations = 34, FaceContinuations = 36;

    internal static uint FrameWords(int boneCount) => Words(ActorHeaderWords + (long)boneCount * MatrixWords + MaxCommands * CommandWords + MaxCommands + MaxRegions);
    internal static uint SurfaceWords(CoverageGpuHostUpload host) => Words(FinalHeaderWords + ((long)host.SourceCount + host.VertexCount) * ModelWords);

    internal static uint WorkWords(CoverageGpuHostUpload h)
    {
        long events = Math.Max(Math.Max(h.SourceCount, h.AliasCount), Math.Max(Math.Max(h.PositionGroupCount, h.VertexCount),
            Math.Max(3L * h.TriangleCount, 2L * h.PositionGroupCount)));
        // Posed and flushed source, four refined rows and a private generated-attribute candidate,
        // one XYZ and three scalar group rows, face scalar/validation rows, six words per event,
        // and typed group/face continuations for the split query phases.
        return Words(WorkHeaderWords + 2L * h.SourceCount * ModelWords + 5L * h.VertexCount * ModelWords
            + (6L + CoverageGpuQueryWorkLayout.ConformWords) * h.PositionGroupCount
            + (3L + CoverageGpuQueryWorkLayout.FaceWords + CoverageGpuNormalWorkLayout.Words) * h.TriangleCount + 6L * events);
    }

    internal static uint[] WorkHeader(CoverageGpuHostUpload h)
    {
        var header = new uint[WorkHeaderWords];
        uint cursor = WorkHeaderWords;
        Row(0, h.SourceCount, ModelWords); Row(2, h.SourceCount, ModelWords);
        for (int row = 4; row <= 10; row += 2) Row(row, h.VertexCount, ModelWords);
        Row(12, h.PositionGroupCount, 3);
        for (int row = 14; row <= 18; row += 2) Row(row, h.PositionGroupCount, 1);
        Row(20, h.TriangleCount, 1); Row(22, h.TriangleCount, 2);
        int events = Math.Max(Math.Max(h.SourceCount, h.AliasCount), Math.Max(Math.Max(h.PositionGroupCount, h.VertexCount),
            Math.Max(checked(3 * h.TriangleCount), checked(2 * h.PositionGroupCount))));
        Row(24, events, 6); Row(AttributeCandidates, h.VertexCount, ModelWords);
        Row(ConformContinuations, h.PositionGroupCount, CoverageGpuQueryWorkLayout.ConformWords);
        Row(FaceContinuations, h.TriangleCount, CoverageGpuQueryWorkLayout.FaceWords);
        Row(CoverageGpuNormalWorkLayout.Pair, h.TriangleCount, CoverageGpuNormalWorkLayout.Words);
        if (cursor != WorkWords(h)) throw new InvalidOperationException("Coverage work layout disagrees with its allocation.");
        return header;
        void Row(int pair, int count, int stride)
        {
            header[pair] = cursor; header[pair + 1] = (uint)count;
            cursor = Words(checked((long)cursor + (long)count * stride));
        }
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
