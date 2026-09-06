using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalFingerprint
{
    private readonly byte[] canonicalBytes;
    private readonly byte[] digest;

    internal JournalFingerprint(ushort formatVersion, byte[] canonicalBytes)
    {
        FormatVersion = formatVersion;
        this.canonicalBytes = (byte[])canonicalBytes.Clone();
        digest = SHA256.HashData(this.canonicalBytes);
    }

    public ushort FormatVersion { get; }
    public ReadOnlyMemory<byte> CanonicalBytes => canonicalBytes;
    public ReadOnlyMemory<byte> Digest => digest;
}

public static class JournalCanonicalizer
{
    public const ushort CurrentFormatVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static JournalFingerprint CreateIntentFingerprint(JournalOperationIdentity identity, ushort formatVersion = CurrentFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireVersion(formatVersion);
        identity.Validate();
        return Create("KJIF", formatVersion, output =>
        {
            WriteField(output, 1, value => WriteGuid(value, identity.OperationId));
            WriteField(output, 2, value => WriteString(value, identity.AuthenticatedScope));
            WriteField(output, 3, value => WriteString(value, identity.ActionKind));
            WriteField(output, 4, value => WritePayload(value, identity.NormalizedIntent.Span));
        });
    }

    public static JournalFingerprint CreateCommitFingerprint(JournalCommit commit, ushort formatVersion = CurrentFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(commit);
        RequireVersion(formatVersion);
        commit.Validate();
        JournalFingerprint intent = CreateIntentFingerprint(commit.Identity, formatVersion);
        return Create("KJEF", formatVersion, output =>
        {
            WriteField(output, 1, value => value.Write(intent.Digest.Span));
            WriteField(output, 2, value => WriteStreams(value, commit.StreamMutations));
            WriteField(output, 3, value => WriteProjections(value, commit.ProjectionWrites));
            WriteField(output, 4, value => WriteString(value, commit.ResultSchema));
            WriteField(output, 5, value => WriteInt32(value, commit.ResultSchemaVersion));
            WriteField(output, 6, value => WritePayload(value, commit.ResultData.Span));
        });
    }

    public static JournalFingerprint CreateInitializationFingerprint(JournalInitialization initialization, ushort formatVersion = CurrentFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        RequireVersion(formatVersion);
        initialization.Validate();
        JournalFingerprint intent = CreateIntentFingerprint(initialization.Identity, formatVersion);
        return Create("KJNF", formatVersion, output =>
        {
            WriteField(output, 1, value => value.Write(intent.Digest.Span));
            WriteField(output, 2, value => WriteString(value, initialization.AbsentStreamKey));
            WriteField(output, 3, value => WriteString(value, initialization.SnapshotSchema));
            WriteField(output, 4, value => WriteInt32(value, initialization.SnapshotSchemaVersion));
            WriteField(output, 5, value => WritePayload(value, initialization.SnapshotData.Span));
            WriteField(output, 6, value => WriteProjections(value, initialization.ProjectionWrites));
            WriteField(output, 7, value => WriteString(value, initialization.ResultSchema));
            WriteField(output, 8, value => WriteInt32(value, initialization.ResultSchemaVersion));
            WriteField(output, 9, value => WritePayload(value, initialization.ResultData.Span));
        });
    }

    public static byte[] ComputeSha256(ReadOnlySpan<byte> value) => SHA256.HashData(value);

    public static bool VerifySha256(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
        => expected.Length == SHA256.HashSizeInBytes && CryptographicOperations.FixedTimeEquals(SHA256.HashData(value), expected);

    private static JournalFingerprint Create(string magic, ushort formatVersion, Action<MemoryStream> fields)
    {
        using var output = new MemoryStream();
        output.Write(StrictUtf8.GetBytes(magic));
        WriteUInt16(output, formatVersion);
        fields(output);
        return new JournalFingerprint(formatVersion, output.ToArray());
    }

    private static void WriteStreams(Stream output, IReadOnlyList<JournalStreamMutation> streams)
    {
        WriteUInt32(output, checked((uint)streams.Count));
        foreach (JournalStreamMutation stream in streams)
        {
            using var entry = new MemoryStream();
            WriteField(entry, 1, value => WriteString(value, stream.StreamKey));
            WriteField(entry, 2, value => WriteInt64(value, stream.ExpectedVersion));
            WriteField(entry, 3, value => WriteUInt32(value, checked((uint)stream.Events.Count)));
            WriteField(entry, 4, value => WriteEvents(value, stream.Events));
            WriteLengthPrefixedEntry(output, entry);
        }
    }

    private static void WriteEvents(Stream output, IReadOnlyList<JournalEvent> events)
    {
        foreach (JournalEvent journalEvent in events)
        {
            using var entry = new MemoryStream();
            WriteField(entry, 1, value => WriteString(value, journalEvent.EventType));
            WriteField(entry, 2, value => WriteInt32(value, journalEvent.EventSchemaVersion));
            WriteField(entry, 3, value => WritePayload(value, journalEvent.Payload.Span));
            WriteLengthPrefixedEntry(output, entry);
        }
    }

    private static void WriteProjections(Stream output, IReadOnlyList<JournalProjectionWrite> projections)
    {
        WriteUInt32(output, checked((uint)projections.Count));
        foreach (JournalProjectionWrite projection in projections)
        {
            using var entry = new MemoryStream();
            WriteField(entry, 1, value => WriteString(value, projection.StreamKey));
            WriteField(entry, 2, value => WriteString(value, projection.SectionName));
            WriteField(entry, 3, value => WriteString(value, projection.ProjectionSchema));
            WriteField(entry, 4, value => WriteInt32(value, projection.ProjectionSchemaVersion));
            WriteField(entry, 5, value => WritePayload(value, projection.Data.Span));
            WriteLengthPrefixedEntry(output, entry);
        }
    }

    private static void WriteField(Stream output, ushort tag, Action<MemoryStream> writeValue)
    {
        using var value = new MemoryStream();
        writeValue(value);
        WriteUInt16(output, tag);
        WriteUInt32(output, checked((uint)value.Length));
        value.Position = 0;
        value.CopyTo(output);
    }

    private static void WriteLengthPrefixedEntry(Stream output, MemoryStream entry)
    {
        WriteUInt32(output, checked((uint)entry.Length));
        entry.Position = 0;
        entry.CopyTo(output);
    }

    private static void WriteGuid(Stream output, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out int written) || written != bytes.Length)
            throw new InvalidOperationException("Guid could not be encoded in RFC 4122 network order.");
        output.Write(bytes);
    }

    private static void WriteString(Stream output, string value) => output.Write(StrictUtf8.GetBytes(value));

    private static void WritePayload(Stream output, ReadOnlySpan<byte> value)
    {
        WriteUInt32(output, checked((uint)value.Length));
        output.Write(value);
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt64(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void RequireVersion(ushort formatVersion)
    {
        if (formatVersion != CurrentFormatVersion)
            throw new NotSupportedException($"Journal fingerprint format version {formatVersion} is not supported.");
    }
}
