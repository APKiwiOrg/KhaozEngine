using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Tools.Coverage;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>LargePanel128: PanelTwo on a 128 joint host whose extra joints carry no vertex influence. Ordinary sample 1
/// is b's pose with key 2, every other ordinary sample copies a's pose with key 1, and the five dense rows match
/// PanelTwo's. Manifests are streamed, so no fixture holds a campaign sized document or every pose.</summary>
internal static class CoverageLargeCampaignFixture
{
    internal const int Joints = 128, Stride = 64 + 64 * Joints + 144, DenseRows = 5;
    internal const int RecordsPerChunk = (8_388_608 - 32) / Stride;
    // Exact affine, nonsingular words for the unused joints, so no record is an identity palette.
    internal static readonly Matrix4x4 Unused = new(1.0000001f, 0, 0, 0, 0, .99999994f, 0, 0, 0, 0, 1.0000001f, 0,
        .12500001f, -.25000003f, .50000006f, 1);
    internal static string Id(int index) => "s" + index.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>Writes the packed campaign: format 2 metadata and KEPP chunks split at the 8 MiB bound.</summary>
    internal static CoverageBuildFiles Create(string scratchDirectory, int ordinaryCount)
    {
        var files = Template(scratchDirectory, out var poses);
        var descriptors = WriteChunks(files, ordinaryCount, poses);
        using (var output = File.Create(files.ManifestPath)) Write(output, files, poses, ordinaryCount, descriptors, dense: true);
        return files;
    }

    /// <summary>Writes the same campaign inline, as format 1, to <paramref name="path"/>. Returns its counted length.</summary>
    internal static long WriteInline(CoverageBuildFiles files, int ordinaryCount, string path, bool dense)
    {
        var poses = Poses(files);
        using var output = File.Create(path);
        using var counter = new CountingStream(output);
        Write(counter, files, poses, ordinaryCount, null, dense);
        return counter.BytesWritten;
    }

    /// <summary>PanelTwo's a and b poses with the 126 unused palette entries replaced, as shared inline templates.</summary>
    static CoverageBuildFiles Template(string scratchDirectory, out JsonObject[] poses)
    {
        var files = new CoverageBuildFiles(scratchDirectory: scratchDirectory, jointCount: Joints);
        poses = Poses(files);
        return files;
    }

    static JsonObject[] Poses(CoverageBuildFiles files)
    {
        var samples = files.Manifest["samples"]!.AsArray();
        foreach (var sample in samples)
        {
            var palette = sample!["palette"]!.AsArray();
            Require(palette.Count == Joints);
            for (int i = 2; i < Joints; i++) palette[i] = CoverageBuildFiles.Matrix(Unused);
        }
        return [samples[0]!.AsObject(), samples[1]!.AsObject()];
    }

    static JsonArray WriteChunks(CoverageBuildFiles files, int ordinaryCount, JsonObject[] poses)
    {
        byte[][] records = poses.Select(p => CoveragePackedFixture.RecordBytes(CoveragePackedFixture.Numeric(p))).ToArray();
        Require(records.All(r => r.Length == Stride));
        int total = ordinaryCount + DenseRows;
        var descriptors = new JsonArray();
        for (int start = 0; start < total; start += RecordsPerChunk)
        {
            int count = Math.Min(RecordsPerChunk, total - start);
            string name = "poses-" + descriptors.Count.ToString("D4", CultureInfo.InvariantCulture) + ".kepp";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] header = new byte[32];
            uint[] words = [0x5050454b, 1, 32, 0x01020304, Joints, 1, (uint)count, Stride];
            for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 * i), words[i]);
            using (var output = File.Create(Path.Combine(files.Folder, name)))
            {
                output.Write(header); hash.AppendData(header);
                for (int i = start; i < start + count; i++)
                {
                    byte[] record = records[i < ordinaryCount ? (i == 1 ? 1 : 0) : (i - ordinaryCount == DenseRows - 1 ? 1 : 0)];
                    output.Write(record); hash.AppendData(record);
                }
            }
            descriptors.Add(new JsonObject { ["path"] = name, ["byteLength"] = 32L + (long)count * Stride,
                ["sha256"] = Convert.ToHexStringLower(hash.GetHashAndReset()), ["recordCount"] = count });
        }
        return descriptors;
    }

    /// <summary>Streams the manifest. Pose arrays are rendered once per template and copied as raw JSON.</summary>
    static void Write(Stream destination, CoverageBuildFiles files, JsonObject[] poses, int ordinaryCount, JsonArray? chunks, bool dense)
    {
        var raw = poses.Select(p => new[] { "actorModel", "palette", "commands" }
            .ToDictionary(n => n, n => JsonSerializer.SerializeToUtf8Bytes(p[n]))).ToArray();
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        foreach (var (name, value) in files.Manifest)
        {
            writer.WritePropertyName(name);
            if (name == "formatVersion") writer.WriteNumberValue(chunks == null ? 1 : 2);
            else if (name == "candidateSampleIds") { writer.WriteStartArray(); writer.WriteStringValue(Id(0)); writer.WriteStringValue(Id(1)); writer.WriteEndArray(); }
            else if (name == "samples")
            {
                writer.WriteStartArray();
                for (int i = 0; i < ordinaryCount; i++) Sample(writer, i == 1 ? 1 : 0, Id(i), true, chunks == null ? null : Reference(i));
                writer.WriteEndArray();
            }
            else if (name == "domains") Domain(writer, value!.AsArray().Single()!.AsObject());
            else value!.WriteTo(writer);
            writer.Flush();
        }
        if (chunks != null) { writer.WritePropertyName("poseChunks"); chunks.WriteTo(writer); }
        writer.WriteEndObject();

        void Domain(Utf8JsonWriter w, JsonObject domain)
        {
            w.WriteStartArray(); w.WriteStartObject();
            foreach (var (name, value) in domain) { w.WritePropertyName(name); value!.WriteTo(w); }
            if (dense)
            {
                w.WriteStartArray("denseRows");
                for (int r = 0; r < DenseRows; r++)
                {
                    w.WriteStartObject(); w.WriteNumber("denseIndex", r); w.WriteNumber("precedingPhaseTick", r / 4);
                    w.WriteNumber("resolvedStateKey", r == DenseRows - 1 ? 2 : 1); w.WritePropertyName("sample");
                    // A dense row repeats its ordinary pose under that pose's sample ID, as in PanelTwo.
                    Sample(w, r == DenseRows - 1 ? 1 : 0, Id(r == DenseRows - 1 ? 1 : 0), false, chunks == null ? null : Reference(ordinaryCount + r));
                    w.WriteBoolean("looped", false); w.WriteBoolean("clamped", false); w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject(); w.WriteEndArray();
        }

        JsonObject Reference(int record) => new() { ["chunk"] = record / RecordsPerChunk, ["record"] = record % RecordsPerChunk };

        void Sample(Utf8JsonWriter w, int pose, string id, bool stateKey, JsonObject? reference)
        {
            w.WriteStartObject();
            foreach (var (name, value) in poses[pose])
            {
                if (name == "stateKey" && !stateKey || reference != null && raw[pose].ContainsKey(name)) continue;
                w.WritePropertyName(name);
                if (name == "id") w.WriteStringValue(id);
                else if (raw[pose].TryGetValue(name, out byte[]? bytes)) w.WriteRawValue(bytes, skipInputValidation: true);
                else value!.WriteTo(w);
            }
            if (reference != null) { w.WritePropertyName("poseRef"); reference.WriteTo(w); }
            w.WriteEndObject();
            if (w.BytesPending > 65_536) w.Flush();
        }
    }

    static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("The large campaign template no longer matches PanelTwo.");
    }

    sealed class CountingStream(Stream destination) : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { destination.Write(buffer); BytesWritten += buffer.Length; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
