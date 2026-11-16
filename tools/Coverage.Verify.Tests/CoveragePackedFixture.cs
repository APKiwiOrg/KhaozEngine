using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

internal static class CoveragePackedFixture
{
    internal static string ScratchRoot => Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-2");

    internal static CoverageBuildFiles Files(int jointCount = 2)
    {
        var files = new CoverageBuildFiles(scratchDirectory: ScratchRoot, jointCount: jointCount);
        var rows = new JsonArray();
        for (int i = 0; i < 5; i++)
        {
            var sample = files.Manifest["samples"]![i == 4 ? 1 : 0]!.DeepClone().AsObject();
            sample.Remove("stateKey");
            rows.Add(new JsonObject { ["denseIndex"] = i, ["precedingPhaseTick"] = i / 4,
                ["resolvedStateKey"] = i == 4 ? 2 : 1, ["sample"] = sample, ["looped"] = false, ["clamped"] = false });
        }
        files.Manifest["domains"]![0]!["denseRows"] = rows;
        files.Write();
        return files;
    }

    internal static void Pack(CoverageBuildFiles files, bool mixed = false)
    {
        var samples = files.Manifest["samples"]!.AsArray().Select(s => s!.AsObject())
            // A domain without dense rows contributes no dense records.
            .Concat(files.Manifest["domains"]!.AsArray().SelectMany(d => d!["denseRows"]?.AsArray() ?? []).Select(r => r!["sample"]!.AsObject())).ToArray();
        var records = new List<byte[]>();
        for (int i = 0; i < samples.Length; i++)
        {
            if (mixed && i == 0) continue;
            var sample = samples[i];
            records.Add(RecordBytes(Numeric(sample)));
            sample.Remove("actorModel"); sample.Remove("palette"); sample.Remove("commands");
            sample["poseRef"] = new JsonObject { ["chunk"] = 0, ["record"] = records.Count - 1 };
        }
        int bones = files.Manifest["inverseBind"]!.AsArray().Count;
        byte[] chunk = new byte[32 + records.Sum(r => r.Length)];
        uint[] header = [0x5050454b, 1, 32, 0x01020304, (uint)bones, 1, (uint)records.Count, (uint)records[0].Length];
        for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(i * 4), header[i]);
        int offset = 32;
        foreach (byte[] record in records) { record.CopyTo(chunk, offset); offset += record.Length; }
        files.Manifest["formatVersion"] = 2;
        var descriptor = files.Save("poses.kepp", chunk);
        descriptor["byteLength"] = chunk.Length;
        descriptor["recordCount"] = records.Count;
        files.Manifest["poseChunks"] = new JsonArray(descriptor);
        files.Write();
    }

    internal static CoverageNumericPose Numeric(JsonObject sample)
    {
        sample = JsonNode.Parse(sample.ToJsonString())!.AsObject();
        static Matrix4x4 Read(JsonNode node) => Matrix(node.AsArray().Select(n => BitConverter.SingleToUInt32Bits(n!.GetValue<float>())).ToArray());
        return new(Read(sample["actorModel"]!), sample["palette"]!.AsArray().Select(m => Read(m!)).ToArray(),
            sample["commands"]!.AsArray().Select(c => new CoveragePackedCommand(c!["id"]!.GetValue<uint>(), c["equipmentInstanceId"]!.GetValue<ulong>(),
                Read(c["equipmentWorld"]!), Read(c["coverageWorld"]!), c["transitionWeight"]!.GetValue<float>())).ToArray());
    }

    internal static void ReverseRecords(CoverageBuildFiles files)
    {
        string path = Path.Combine(files.Folder, "poses.kepp");
        byte[] original = File.ReadAllBytes(path), reversed = original.ToArray();
        int count = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(24));
        int stride = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(28));
        for (int i = 0; i < count; i++) original.AsSpan(32 + i * stride, stride).CopyTo(reversed.AsSpan(32 + (count - i - 1) * stride));
        var samples = files.Manifest["samples"]!.AsArray().Concat(files.Manifest["domains"]!.AsArray()
            .SelectMany(d => d!["denseRows"]!.AsArray()).Select(r => r!["sample"]));
        foreach (var sample in samples)
            if (sample!["poseRef"] is { } reference) reference["record"] = count - reference["record"]!.GetValue<int>() - 1;
        File.WriteAllBytes(path, reversed);
        files.Manifest["poseChunks"]![0]!["sha256"] = CoverageBuildInputJson.Hash(reversed);
        files.Write();
    }

    internal static byte[] SampleBytes(CoverageSample sample) => RecordBytes(new(sample.ActorModel, sample.Palette.ToArray(),
        sample.Commands.ToArray().Select(c => new CoveragePackedCommand(c.Id, c.EquipmentInstanceId, c.EquipmentWorld, c.CoverageWorld, c.TransitionWeight)).ToArray()));

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
