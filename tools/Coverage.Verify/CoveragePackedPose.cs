using System.Numerics;

namespace KhaozEngine.Tools.Coverage;

internal readonly record struct CoveragePackedHeader(int PaletteCount, int CommandCount, int RecordCount, int RecordStride);
internal sealed record CoveragePackedChunkReference(string Path, long ByteLength, string Sha256, int RecordCount);
internal readonly record struct CoveragePoseReference(int ChunkIndex, int RecordIndex);
internal readonly record struct CoverageInputLocation(string Path, string Identity, int? ChunkIndex = null, int? RecordIndex = null);
internal readonly record struct CoveragePackedCommand(uint Id, ulong EquipmentInstanceId,
    Matrix4x4 EquipmentWorld, Matrix4x4 CoverageWorld, float TransitionWeight);
internal sealed record CoverageNumericPose(Matrix4x4 ActorModel, Matrix4x4[] Palette, CoveragePackedCommand[] Commands);
