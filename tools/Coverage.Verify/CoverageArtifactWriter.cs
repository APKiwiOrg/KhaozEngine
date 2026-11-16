using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tools.Coverage;

internal static class CoverageArtifactWriter
{
    internal static CoverageStagedArtifact Stage(string destinationPath, CoverageArtifactHeader header,
        Action<Stream> writePayload, string scratchDirectory, int artifactByteLimit = 67_108_864, int partByteLimit = 67_108_864)
    {
        if (artifactByteLimit is < 1 or > CoverageArtifactSet.MaxBytes) throw new ArgumentOutOfRangeException(nameof(artifactByteLimit));
        if (partByteLimit < 1 || partByteLimit > artifactByteLimit) throw new ArgumentOutOfRangeException(nameof(partByteLimit));
        string destination = Path.GetFullPath(destinationPath), scratch = CoverageArtifactSet.Scratch(scratchDirectory);
        try
        {
            string payload = Path.Combine(scratch, "payload");
            long entries = Math.Max(header.Totals.GetValueOrDefault("entries", 0), header.Totals.GetValueOrDefault("candidateEntries", 0));
            long capacity = Math.Min((long)CoverageArtifactSet.MaxArtifactParts * partByteLimit,
                CoverageArtifactSet.MaximumPayloadBytes(CoverageArtifactSet.MaxBytes,
                    (int)Math.Min(4096, entries)));
            using (var output = BoundedOutput.Create(payload, Math.Max(artifactByteLimit, capacity))) writePayload(output);
            long length = new FileInfo(payload).Length;
            string payloadHash = CoverageArtifactSet.HashFile(payload);
            using (var check = File.OpenRead(payload)) CoverageLogicalDocumentReader.Validate(check, header.Kind).Match(header);
            if (length <= artifactByteLimit) return CoverageStagedArtifact.Create(destination, payload, scratch, header, []);
            CoverageArtifactSet.Capacity((length + partByteLimit - 1) / partByteLimit, CoverageArtifactSet.MaxArtifactParts);
            int count = checked((int)((length + partByteLimit - 1) / partByteLimit));
            string placeholder = new('0', 64);
            var estimated = Enumerable.Range(0, count).Select(i => new CoverageArtifactPart(i,
                CoverageArtifactSet.PartPath(destination, payloadHash, i, placeholder), Math.Min(partByteLimit, length - (long)i * partByteLimit), placeholder, "", ""));
            using (var preflight = new BoundedOutput(Stream.Null, artifactByteLimit)) WriteIndex(preflight, header, length, payloadHash, count, estimated);
            var parts = Split(payload, destination, payloadHash, scratch, partByteLimit);
            string root = Path.Combine(scratch, "root");
            using (var output = BoundedOutput.Create(root, artifactByteLimit))
                WriteIndex(output, header, length, payloadHash, parts.Count, parts);
            File.Delete(payload);
            return CoverageStagedArtifact.Create(destination, root, scratch, header, parts);
        }
        catch { Directory.Delete(scratch, true); throw; }
    }
    static void WriteIndex(Stream output, CoverageArtifactHeader header, long length, string payloadHash, int count, IEnumerable<CoverageArtifactPart> parts)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject(); writer.WriteString("schema", CoverageArtifactSet.Schema(header.Kind));
        CoverageArtifactSet.WriteHeader(writer, header);
        writer.WriteNumber("payloadByteLength", length); writer.WriteString("payloadSha256", payloadHash);
        writer.WriteNumber("partCount", count); writer.WriteStartArray("parts");
        foreach (var part in parts)
        {
            writer.WriteStartObject(); writer.WriteNumber("ordinal", part.Ordinal); writer.WriteString("path", part.Path);
            writer.WriteNumber("byteLength", part.ByteLength); writer.WriteString("sha256", part.Sha256); writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }
    static List<CoverageArtifactPart> Split(string payload, string destination, string payloadHash, string scratch, int limit)
    {
        var parts = new List<CoverageArtifactPart>();
        byte[] buffer = new byte[CoverageArtifactSet.BufferBytes];
        using var input = File.OpenRead(payload);
        while (input.Position < input.Length)
        {
            int ordinal = parts.Count;
            string stage = Path.Combine(scratch, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            long remaining = Math.Min(limit, input.Length - input.Position), length = remaining;
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                while (remaining > 0)
                {
                    int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) throw new EndOfStreamException();
                    output.Write(buffer, 0, count); remaining -= count;
                }
                output.Flush(true);
            }
            string hash = CoverageArtifactSet.HashFile(stage);
            string relative = CoverageArtifactSet.PartPath(destination, payloadHash, ordinal, hash);
            parts.Add(new(ordinal, relative, length, hash, Path.Combine(Path.GetDirectoryName(destination)!, relative), stage));
        }
        return parts;
    }

    /// <summary>Counts every write path, because Stream routes WriteByte and asynchronous writes through Write.</summary>
    sealed class BoundedOutput(Stream inner, long limit) : Stream
    {
        long length;
        internal static BoundedOutput Create(string path, long limit) =>
            new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), limit);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CoverageArtifactSet.Capacity(length + buffer.Length, limit);
            inner.Write(buffer); length += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
