using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore.Journal;

public interface IMutationJournalStore
{
    Task<JournalOperationResolution> ResolveOperationAsync(JournalOperationIdentity identity, CancellationToken cancellationToken = default);
    Task<JournalInitializeResult> InitializeAsync(JournalInitialization initialization, CancellationToken cancellationToken = default);
    Task<JournalCommitResult> CommitAsync(JournalCommit commit, CancellationToken cancellationToken = default);
    Task<JournalSnapshot?> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default);
    Task<JournalEventPage> ReadEventsAsync(JournalEventRead read, CancellationToken cancellationToken = default);
    Task<JournalProjectionRead> ReadProjectionsAsync(JournalProjectionQuery query, CancellationToken cancellationToken = default);
    Task<JournalCompactionResult> CompactAsync(JournalCompaction compaction, CancellationToken cancellationToken = default);
}

internal static class JournalProjectionCursor
{
    private const byte FormatVersion = 1;

    internal static string Encode(Guid epoch, string streamKey, long headVersion)
    {
        byte[] stream = Encoding.ASCII.GetBytes(streamKey);
        var bytes = new byte[25 + stream.Length];
        bytes[0] = FormatVersion;
        if (!epoch.TryWriteBytes(bytes.AsSpan(1, 16), bigEndian: true, out int written) || written != 16)
            throw new InvalidOperationException("Store epoch could not be encoded.");
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(17, 8), headVersion);
        stream.CopyTo(bytes, 25);
        return Convert.ToBase64String(bytes);
    }

    internal static bool TryDecode(string? cursor, out Guid epoch, out string streamKey, out long headVersion)
    {
        epoch = Guid.Empty;
        streamKey = string.Empty;
        headVersion = 0;
        if (cursor is null || cursor.Length > 512) return false;
        try
        {
            byte[] bytes = Convert.FromBase64String(cursor);
            if (bytes.Length <= 25 || bytes[0] != FormatVersion) return false;
            epoch = new Guid(bytes.AsSpan(1, 16), bigEndian: true);
            headVersion = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(17, 8));
            if (headVersion < 0) return false;
            streamKey = Encoding.ASCII.GetString(bytes, 25, bytes.Length - 25);
            JournalValidation.StreamKey(streamKey);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static (Guid Epoch, string StreamKey, long HeadVersion) DecodeForTest(string cursor)
    {
        if (!TryDecode(cursor, out Guid epoch, out string streamKey, out long headVersion))
            throw new FormatException("Projection cursor is invalid.");
        return (epoch, streamKey, headVersion);
    }
}
