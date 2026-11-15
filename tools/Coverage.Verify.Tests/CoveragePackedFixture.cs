using System;
using System.Buffers.Binary;
using System.Numerics;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

internal static class CoveragePackedFixture
{
    internal const string HeaderHex = "4b45505001000000200000000403020102000000010000000100000050010000";
    internal static readonly uint[] FiniteWords =
        [0x00000000, 0x80000000, 0x00000001, 0x80000001, 0x007fffff, 0x807fffff, 0x00800000, 0x3f800001];

    internal static CoverageNumericPose Pose(bool wordVector = false)
    {
        if (!wordVector) return new(Matrix4x4.Identity, [Matrix4x4.Identity, Matrix4x4.Identity],
            [new(23, 17, Matrix4x4.Identity, Matrix4x4.Identity, 1f)]);
        Matrix4x4 At(int start)
        {
            var words = new uint[16];
            for (int i = 0; i < words.Length; i++)
                words[i] = start + i < FiniteWords.Length ? FiniteWords[start + i] : 0x3f800000u + (uint)(start + i);
            return Matrix(words);
        }
        return new(At(0), [At(16), At(32)], [new(23, 17, At(48), At(64), BitConverter.UInt32BitsToSingle(FiniteWords[3]))]);
    }

    internal static Matrix4x4 Matrix(uint[] words)
    {
        float F(int index) => BitConverter.UInt32BitsToSingle(words[index % words.Length]);
        return new(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
    }

    internal static byte[] ChunkBytes(bool wordVector = false)
    {
        byte[] bytes = new byte[368];
        Convert.FromHexString(HeaderHex).CopyTo(bytes, 0);
        RecordBytes(Pose(wordVector)).CopyTo(bytes, 32);
        return bytes;
    }

    // Independent fixture encoding never calls the codec under test.
    internal static byte[] RecordBytes(CoverageNumericPose pose)
    {
        byte[] bytes = new byte[64 + 64 * pose.Palette.Length + 144 * pose.Commands.Length];
        int offset = 0;
        void Word(uint word) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), word); offset += 4; }
        void WriteMatrix(Matrix4x4 m)
        {
            float[] values = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            foreach (float value in values) Word(BitConverter.SingleToUInt32Bits(value));
        }
        WriteMatrix(pose.ActorModel);
        foreach (var matrix in pose.Palette) WriteMatrix(matrix);
        foreach (var command in pose.Commands)
        {
            Word(command.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), command.EquipmentInstanceId);
            offset += 8;
            WriteMatrix(command.EquipmentWorld);
            WriteMatrix(command.CoverageWorld);
            Word(BitConverter.SingleToUInt32Bits(command.TransitionWeight));
        }
        return bytes;
    }
}
