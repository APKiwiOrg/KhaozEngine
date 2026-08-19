using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The one walker every cell-blob migration and the persistence driver's bring-forward pass share: it parses a
/// persisted snapshot body frame by frame at a stated wire generation's built-in layout
/// (<see cref="BuiltinBlobLayout"/>) and re-emits it at another, optionally widening 32-bit entity ids on the way.
/// <para>
/// Both engine migrations used to hand-roll this walk with their own hard-coded payload table, which is exactly how
/// one of them ended up describing a movement frame that had grown six times since (#353). Everything that needs to
/// find an entity boundary in a blob goes through here now.
/// </para>
/// <para>
/// The walk is a strong check on whether the stated generation is the right one: it re-reads every type id, every
/// length prefix and the per-entity terminator, and it insists the last entity ends exactly at the last byte. That
/// check plus the frame-count tie-break is what lets <see cref="RewriteInferring"/> recover a blob whose generation
/// was never recorded.
/// </para>
/// </summary>
internal static class CellBlobRewriter
{
    /// <summary>Pass as the to-generation to emit at whatever generation the body turned out to be at (a purely
    /// structural rewrite, e.g. the netId widening, which must not also move the component layout).</summary>
    internal const int KeepSourceGeneration = 0;

    /// <summary>
    /// Rewrites <paramref name="body"/> after inferring which wire generation in
    /// <paramref name="oldestGeneration"/>..<paramref name="newestGeneration"/> it was written at. Throws when no
    /// candidate walks the whole body, so the driver quarantines the bytes.
    /// <para>
    /// More than one candidate can walk a body cleanly, and picking the newest is NOT safe: a movement payload read
    /// too long simply swallows the frames that follow it, and the sizes involved are small enough that it lands back
    /// on a boundary quite easily (a generation-3 movement followed by a six-character display name is exactly a
    /// generation-8 movement's worth of bytes, which is how this was found). So every candidate is tried and the one
    /// that recovers the MOST component frames wins, newest first on a tie. An over-long read can only ever swallow
    /// frames, never produce extra ones, so the true generation always scores at least as high as any candidate above
    /// it. This is still a heuristic on a body whose generation nobody recorded, which is precisely why schema
    /// v<see cref="WireGenerationBlobMigration.StampedSchemaVersion"/> stamps the generation into the header and
    /// stops anything written from now on needing to be guessed at.
    /// </para>
    /// </summary>
    internal static byte[] RewriteInferring(byte[] body, int oldestGeneration, int newestGeneration,
        int toGeneration, bool widenNetIds, string schemaLabel)
    {
        ArgumentNullException.ThrowIfNull(body);
        byte[]? best = null;
        int bestFrames = -1;
        for (int from = newestGeneration; from >= oldestGeneration; from--)
        {
            int to = toGeneration == KeepSourceGeneration ? from : toGeneration;
            if (!TryRewrite(body, from, to, widenNetIds, out byte[]? rewritten, out int frames)) continue;
            if (frames > bestFrames) { best = rewritten; bestFrames = frames; }   // strict >, so a tie keeps the newer
        }
        if (best is not null) return best;

        throw new InvalidOperationException(
            $"Snapshot body does not walk as a {schemaLabel} blob at any wire generation " +
            $"{oldestGeneration}..{newestGeneration}; cannot migrate.");
    }

    /// <summary>Rewrites <paramref name="body"/> from a KNOWN wire generation, throwing when it does not walk.</summary>
    internal static byte[] Rewrite(byte[] body, int fromGeneration, int toGeneration, bool widenNetIds)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (TryRewrite(body, fromGeneration, toGeneration, widenNetIds, out byte[]? rewritten, out _)) return rewritten!;
        throw new InvalidOperationException(
            $"Snapshot body does not walk at wire generation {fromGeneration}; cannot bring it to {toGeneration}.");
    }

    /// <summary>
    /// The walk. Returns false (and a null <paramref name="result"/>) on ANY malformed read rather than throwing, so
    /// a caller can try one candidate generation after another cheaply. <paramref name="frameCount"/> reports how many
    /// component frames the walk recovered, which is how <see cref="RewriteInferring"/> tells two clean walks apart.
    /// </summary>
    internal static bool TryRewrite(byte[] body, int fromGeneration, int toGeneration, bool widenNetIds,
        out byte[]? result, out int frameCount)
    {
        result = null;
        frameCount = 0;
        if (toGeneration < fromGeneration) return false;   // a body is never walked backwards onto an older layout

        int pos = 0;
        if (!TryReadInt32(body, ref pos, out int count) || count < 0) return false;
        // An entity costs at least its id plus the empty-component terminator, so a count that cannot possibly fit
        // is a mis-walk (or garbage) and is rejected before allocating anything for it.
        int minEntityBytes = (widenNetIds ? 4 : 8) + 2;
        if ((long)count * minEntityBytes > body.Length - pos) return false;

        using var output = new MemoryStream(body.Length + 32);
        using var bw = new BinaryWriter(output);
        bw.Write(count);   // the entity count itself never changes width

        for (int i = 0; i < count; i++)
        {
            if (widenNetIds)
            {
                if (!TryReadInt32(body, ref pos, out int narrowId)) return false;
                // Widen into node 0's low-48 counter space. Reading UNSIGNED keeps a counter past 2^31 positive; a
                // normal id (1, 2, 3, ...) is numerically unchanged.
                bw.Write((long)(uint)narrowId);
            }
            else
            {
                if (!TryReadInt64(body, ref pos, out long netId)) return false;
                bw.Write(netId);
            }
            if (!TryRewriteComponents(body, ref pos, bw, fromGeneration, toGeneration, ref frameCount)) return false;
        }

        if (pos != body.Length) return false;   // trailing bytes: this candidate walked the body wrong

        bw.Flush();
        result = output.ToArray();
        return true;
    }

    // One entity's component-frame stream, up to and including the [ushort 0] terminator.
    private static bool TryRewriteComponents(byte[] body, ref int pos, BinaryWriter bw, int fromGeneration,
        int toGeneration, ref int frameCount)
    {
        while (true)
        {
            if (!TryReadUInt16(body, ref pos, out ushort typeId)) return false;
            bw.Write(typeId);
            if (typeId == 0) return true;   // end-of-entity terminator
            frameCount++;

            if (ReplicationRegistry.IsExtension(typeId))
            {
                // Consumer extension frames carry their own length and are opaque to the engine: copied verbatim.
                if (!TryRead7BitEncodedInt(body, ref pos, out int len) || len < 0) return false;
                bw.Write7BitEncodedInt(len);
                if (!TryCopy(body, ref pos, bw, len)) return false;
                continue;
            }

            if (typeId == MoveProtocol.IdentityTypeId)
            {
                // [ushort byteLen][byteLen UTF-8 bytes]. MoveProtocol truncates the name to MaxDisplayNameBytes on
                // write, so a longer prefix means this candidate generation walked the body wrong (or the bytes are
                // not a snapshot at all) rather than being a name to copy.
                if (!TryReadUInt16(body, ref pos, out ushort nameLen) || nameLen > MoveProtocol.MaxDisplayNameBytes) return false;
                bw.Write(nameLen);
                if (!TryCopy(body, ref pos, bw, nameLen)) return false;
                continue;
            }

            int fromLen = BuiltinBlobLayout.PayloadLength(typeId, fromGeneration);
            if (fromLen < 0) return false;   // absent at this generation, or an id the engine does not own
            int toLen = BuiltinBlobLayout.PayloadLength(typeId, toGeneration);
            if (toLen < fromLen) return false;

            if (typeId == MoveProtocol.PositionTypeId && toLen != fromLen)
            {
                // The one restructure rather than an append: stamp WorldFrame.Origin ahead of the untouched absolute
                // triple. Origin's anchor is exactly Vector3.Zero, so {Origin, absolute} denotes the identical world
                // position, and the owning cell rebases it into its own frame on restore.
                bw.Write((short)0);   // frame X
                bw.Write((short)0);   // frame Z
                if (!TryCopy(body, ref pos, bw, fromLen)) return false;
                continue;
            }

            // Every other built-in grew by APPENDING fields whose default encoding is all-zero bytes, so bringing the
            // payload forward is the stored bytes followed by the zeros the newer fields would have written.
            if (!TryCopy(body, ref pos, bw, fromLen)) return false;
            for (int pad = fromLen; pad < toLen; pad++) bw.Write((byte)0);
        }
    }

    private static bool TryCopy(byte[] body, ref int pos, BinaryWriter bw, int len)
    {
        if (len < 0 || (long)pos + len > body.Length) return false;
        bw.Write(body, pos, len);
        pos += len;
        return true;
    }

    private static bool TryReadInt32(byte[] body, ref int pos, out int value)
    {
        value = 0;
        if (pos + 4 > body.Length) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos, 4));
        pos += 4;
        return true;
    }

    private static bool TryReadInt64(byte[] body, ref int pos, out long value)
    {
        value = 0;
        if (pos + 8 > body.Length) return false;
        value = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(pos, 8));
        pos += 8;
        return true;
    }

    private static bool TryReadUInt16(byte[] body, ref int pos, out ushort value)
    {
        value = 0;
        if (pos + 2 > body.Length) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2));
        pos += 2;
        return true;
    }

    // BinaryWriter.Write7BitEncodedInt's format, read without the throwing BinaryReader: at most five bytes, each
    // carrying seven bits, the last with its high bit clear.
    private static bool TryRead7BitEncodedInt(byte[] body, ref int pos, out int value)
    {
        value = 0;
        for (int shift = 0; shift < 32; shift += 7)
        {
            if (pos >= body.Length) return false;
            byte b = body[pos++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;   // a sixth continuation byte is not a valid encoding
    }
}
