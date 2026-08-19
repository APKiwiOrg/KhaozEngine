using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
/// length prefix and the per-entity terminator, and it insists the last entity ends exactly at the last byte. On top
/// of that, <see cref="RewriteInferring"/> judges each candidate against everything the WRITER is known to do
/// (<see cref="CellBlobWalkPolicy"/>) and refuses to choose between two candidates that both survive, because there
/// is no scoring rule that is safe in both directions - see <see cref="AmbiguousCellBlobGenerationException"/>.
/// </para>
/// </summary>
internal static class CellBlobRewriter
{
    /// <summary>Pass as the to-generation to emit at whatever generation the body turned out to be at (a purely
    /// structural rewrite, e.g. the netId widening, which must not also move the component layout).</summary>
    internal const int KeepSourceGeneration = 0;

    /// <summary>
    /// Rewrites <paramref name="body"/> after inferring which wire generation in
    /// <paramref name="oldestGeneration"/>..<paramref name="newestGeneration"/> it was written at.
    /// <para>
    /// Every candidate is walked under <paramref name="policy"/>, which discards the ones that recover something no
    /// build writes (an unregistered extension id, a built-in out of registration order or repeated, a movement bool
    /// byte that is not 0 or 1, a display name that is not UTF-8). Then exactly one of three things happens: no
    /// candidate survives and this throws <see cref="InvalidOperationException"/>, every survivor produces the same
    /// bytes and those are returned, or the survivors disagree and this throws
    /// <see cref="AmbiguousCellBlobGenerationException"/>. It never picks.
    /// </para>
    /// <para>
    /// One of those rules can be wrong about the whole body rather than about a candidate. The registry rule reads
    /// "nobody registered this id", and a RETAINED unknown extension frame is exactly that while still being bytes a
    /// real build wrote - retain-and-rewrite carries a dropped id forward verbatim. On such a body the rule retires
    /// every candidate at once, so supplying a registry turned a blob that migrates cleanly without one into a
    /// quarantine labelled corrupt. When the registry rule is what emptied the field, the decision is taken again
    /// with that one rule dropped (<see cref="CellBlobWalkPolicy.WithoutRegistry"/>) and the same three outcomes
    /// apply. The registry stays a candidate REMOVER either way: it can turn an ambiguity into a migration, and it
    /// can never cost a blob an unsupplied registry would have migrated.
    /// </para>
    /// <para>
    /// The scoring rule this replaced (keep the parse that recovers the MOST frames) was unsafe in the under-read
    /// direction. A candidate OLDER than the truth reads a built-in payload short, and the bytes it leaves behind
    /// re-sync into frames the walk happily copies, so it can outscore the truth and produce a structurally valid
    /// body that decodes into wrong movement fields and phantom components. Schema
    /// v<see cref="WireGenerationBlobMigration.StampedSchemaVersion"/> stamps the generation into the header, so
    /// nothing written from now on is inferred at all.
    /// </para>
    /// </summary>
    internal static byte[] RewriteInferring(byte[] body, int oldestGeneration, int newestGeneration,
        int toGeneration, bool widenNetIds, string schemaLabel, CellBlobWalkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(body);
        // A single-candidate range is not an inference at all (the v1 netId widening): judging it on the evidence
        // rules could only ever turn a blob that decodes into a quarantine, with nothing to choose instead.
        CellBlobWalkPolicy effective = newestGeneration > oldestGeneration ? policy : CellBlobWalkPolicy.Structural;

        List<ushort>? unregistered = null;
        if (effective.KnownExtensionId is not null)
        {
            unregistered = new List<ushort>(2);
            effective = effective.WatchingUnregistered(unregistered);
        }

        Verdict verdict = Decide(body, oldestGeneration, newestGeneration, toGeneration, widenNetIds, effective);
        // Nothing survived, and the registry rule rejected at least one frame on the way: that is the retained
        // unknown extension frame described above, not a body that fails to parse. Decide again without that rule.
        if (verdict.Survivor is null && !verdict.Disagree && unregistered is { Count: > 0 })
            verdict = Decide(body, oldestGeneration, newestGeneration, toGeneration, widenNetIds,
                effective.WithoutRegistry());

        if (verdict.Disagree) throw new AmbiguousCellBlobGenerationException(schemaLabel, verdict.Generations!);
        if (verdict.Survivor is not null) return verdict.Survivor;

        throw new InvalidOperationException(
            NoCandidateMessage(schemaLabel, oldestGeneration, newestGeneration, unregistered));
    }

    // What one pass over the candidate range came to. Survivor null with Disagree false means the field is empty.
    private readonly record struct Verdict(byte[]? Survivor, List<int>? Generations, bool Disagree);

    private static Verdict Decide(byte[] body, int oldestGeneration, int newestGeneration, int toGeneration,
        bool widenNetIds, CellBlobWalkPolicy policy)
    {
        byte[]? survivor = null;
        List<int>? survivingGenerations = null;
        bool disagree = false;
        for (int from = oldestGeneration; from <= newestGeneration; from++)
        {
            int to = toGeneration == KeepSourceGeneration ? from : toGeneration;
            if (!TryRewrite(body, from, to, widenNetIds, policy, out byte[]? rewritten)) continue;
            (survivingGenerations ??= new List<int>(2)).Add(from);
            if (survivor is null) { survivor = rewritten; continue; }
            // Two candidates that produce the SAME bytes are not a choice: the generations differ only in a field
            // this body does not carry (a gen-7 and a gen-8 body without a pickup frame are byte-identical).
            if (!rewritten.AsSpan().SequenceEqual(survivor)) disagree = true;
        }
        return new Verdict(survivor, survivingGenerations, disagree);
    }

    // The message an operator reads before deciding what to do with the cell, so it names the cause it can act on
    // (the ids the supplied registry did not know) and both knobs that can bring the blob in.
    private static string NoCandidateMessage(string schemaLabel, int oldestGeneration, int newestGeneration,
        List<ushort>? unregistered)
    {
        string retried = unregistered is { Count: > 0 }
            ? $" The walk recovered extension id(s) {string.Join(", ", unregistered)}, which the supplied " +
              $"{nameof(CellBlobMigrationOptions)}.{nameof(CellBlobMigrationOptions.Registry)} does not know, and " +
              "deciding again without that rule did not find a candidate either."
            : string.Empty;
        return $"Snapshot body does not walk as a {schemaLabel} blob at any wire generation " +
            $"{oldestGeneration}..{newestGeneration}, so it cannot be migrated." + retried +
            $" Set {nameof(CellBlobMigrationOptions)}.{nameof(CellBlobMigrationOptions.AssumedWireGeneration)} to " +
            "the generation the writing build was at, and pass that build's live registry as " +
            $"{nameof(CellBlobMigrationOptions)}.{nameof(CellBlobMigrationOptions.Registry)}, so the walk has a " +
            "stated generation to read the body at instead of a range to choose from.";
    }

    /// <summary>Rewrites <paramref name="body"/> from a KNOWN wire generation, throwing when it does not walk. The
    /// walk is structural only: a recorded generation is not evidence to be weighed, and a body carrying an extension
    /// id this build's registry has dropped must still come forward (retain-and-rewrite).</summary>
    internal static byte[] Rewrite(byte[] body, int fromGeneration, int toGeneration, bool widenNetIds)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (TryRewrite(body, fromGeneration, toGeneration, widenNetIds, CellBlobWalkPolicy.Structural,
            out byte[]? rewritten)) return rewritten!;
        throw new InvalidOperationException(
            $"Snapshot body does not walk at wire generation {fromGeneration}; cannot bring it to {toGeneration}.");
    }

    /// <summary>
    /// The walk. Returns false (and a null <paramref name="result"/>) on ANY malformed read, or on anything
    /// <paramref name="policy"/> rejects, rather than throwing - so a caller can try one candidate generation after
    /// another cheaply.
    /// </summary>
    internal static bool TryRewrite(byte[] body, int fromGeneration, int toGeneration, bool widenNetIds,
        CellBlobWalkPolicy policy, out byte[]? result)
    {
        result = null;
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
            if (!TryRewriteComponents(body, ref pos, bw, fromGeneration, toGeneration, policy)) return false;
        }

        if (pos != body.Length) return false;   // trailing bytes: this candidate walked the body wrong

        bw.Flush();
        result = output.ToArray();
        return true;
    }

    // One entity's component-frame stream, up to and including the [ushort 0] terminator.
    private static bool TryRewriteComponents(byte[] body, ref int pos, BinaryWriter bw, int fromGeneration,
        int toGeneration, in CellBlobWalkPolicy policy)
    {
        var frames = new CellBlobEntityFrames();
        while (true)
        {
            if (!TryReadUInt16(body, ref pos, out ushort typeId)) return false;
            if (typeId == 0) { bw.Write(typeId); return true; }   // end-of-entity terminator
            if (!frames.Accept(typeId, policy)) return false;
            bw.Write(typeId);

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
                // [ushort byteLen][byteLen UTF-8 bytes]. The prefix being within MoveProtocol.MaxDisplayNameBytes
                // is a property of the WRITER, not of the format, so it is one of the policy's evidence rules
                // (AcceptsDisplayName) rather than part of the structural walk: a recorded generation is decoded,
                // never judged, and this walk must not refuse a name a build with a different cap wrote.
                if (!TryReadUInt16(body, ref pos, out ushort nameLen)) return false;
                if ((long)pos + nameLen > body.Length || !policy.AcceptsDisplayName(body, pos, nameLen)) return false;
                bw.Write(nameLen);
                if (!TryCopy(body, ref pos, bw, nameLen)) return false;
                continue;
            }

            int fromLen = BuiltinBlobLayout.PayloadLength(typeId, fromGeneration);
            if (fromLen < 0) return false;   // absent at this generation, or an id the engine does not own
            int toLen = BuiltinBlobLayout.PayloadLength(typeId, toGeneration);
            if (toLen < fromLen) return false;
            if ((long)pos + fromLen > body.Length) return false;

            if (typeId == MoveProtocol.MovementTypeId && !policy.AcceptsMovementPayload(body, pos, fromGeneration))
                return false;

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
