using System;
using System.Collections.Generic;
using System.Text.Unicode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// How strictly <see cref="CellBlobRewriter"/> judges a frame while it walks a stored body.
/// <para>
/// Two settings, and the difference between them is whether the generation is KNOWN. When the header records it (or
/// an operator states it), the walk is a decode: it must accept anything a real build wrote, including an extension
/// id this build's registry has since dropped, because retain-and-rewrite carries those forward verbatim. When the
/// generation is being INFERRED, every accepted frame is evidence for a candidate, so the walk applies every check
/// that is true of a body a real build wrote, and a candidate that trips one is discarded rather than scored.
/// </para>
/// <para>
/// Each strict rule below is a property of the writer, not a heuristic:
/// <list type="bullet">
/// <item>Component frames within one entity are written in registry order, and
/// <see cref="MoveProtocol.CreateRegistry"/> registers every built-in before it hands the registry to the consumer,
/// so built-in ids ascend and none follows an extension frame (retained unknown frames are appended last).</item>
/// <item>An entity carries a given component at most once, so no type id repeats within it.</item>
/// <item>An extension id the live registry does not know is far more likely to be a mis-walk's re-synced garbage than
/// a component (measured on the #353 fix round: every mis-inference recovered at least one such id).</item>
/// <item>A <see cref="MovementState"/> payload's bool fields are written by <c>BinaryWriter.Write(bool)</c>, so those
/// bytes are 0 or 1 and nothing else.</item>
/// <item>A display name is UTF-8 that <see cref="MoveProtocol"/> truncated at a character boundary, so it decodes
/// strictly.</item>
/// </list>
/// </para>
/// </summary>
internal readonly struct CellBlobWalkPolicy
{
    private CellBlobWalkPolicy(bool strict, Func<ushort, bool>? knownExtensionId)
    {
        Strict = strict;
        KnownExtensionId = knownExtensionId;
    }

    /// <summary>Shape only: the walk rejects what it cannot parse and judges nothing else. For a generation that is
    /// recorded rather than guessed.</summary>
    internal static CellBlobWalkPolicy Structural => default;

    /// <summary>Every check the writer's own behaviour licenses, for a walk that is deciding WHICH generation wrote
    /// the body. <paramref name="knownExtensionId"/> is the live registry's membership test, or null when the caller
    /// has no registry to offer (the rest of the rules still apply).</summary>
    internal static CellBlobWalkPolicy Inferring(Func<ushort, bool>? knownExtensionId) => new(true, knownExtensionId);

    /// <summary>Whether the evidence rules above are applied on top of the structural parse.</summary>
    internal bool Strict { get; }

    /// <summary>The live registry's membership test, when one was supplied.</summary>
    internal Func<ushort, bool>? KnownExtensionId { get; }

    /// <summary>Whether a movement payload at <paramref name="pos"/> has valid bool bytes for
    /// <paramref name="wireGeneration"/>. The caller has already bounds-checked the payload.</summary>
    internal bool AcceptsMovementPayload(byte[] body, int pos, int wireGeneration)
    {
        if (!Strict) return true;
        if (body[pos + BuiltinBlobLayout.MovementGroundedOffset] > 1) return false;
        return wireGeneration < BuiltinBlobLayout.SwimmingWireGeneration
            || body[pos + BuiltinBlobLayout.MovementSwimmingOffset] <= 1;
    }

    /// <summary>Whether the display-name bytes at <paramref name="pos"/> are the UTF-8 the identity codec writes.</summary>
    internal bool AcceptsDisplayName(byte[] body, int pos, int len) =>
        !Strict || Utf8.IsValid(body.AsSpan(pos, len));
}

/// <summary>
/// The per-entity half of <see cref="CellBlobWalkPolicy"/>: what this entity's frame stream has produced so far, so
/// the ordering and no-repeat rules can be applied as each type id is read. Reset per entity by the walk.
/// </summary>
internal struct CellBlobEntityFrames
{
    private ushort lastBuiltinId;
    private bool sawExtension;
    private List<ushort>? extensionIds;

    /// <summary>Records <paramref name="typeId"/> as this entity's next frame, or returns false when it cannot be
    /// (which retires the candidate generation being walked). Never rejects under
    /// <see cref="CellBlobWalkPolicy.Structural"/>.</summary>
    internal bool Accept(ushort typeId, in CellBlobWalkPolicy policy)
    {
        if (!ReplicationRegistry.IsExtension(typeId))
        {
            // Built-ins are written first and in registration order, so a repeat or a step backwards is a mis-walk,
            // and so is a built-in that follows a length-prefixed extension frame.
            if (policy.Strict && (sawExtension || typeId <= lastBuiltinId)) return false;
            lastBuiltinId = typeId;
            return true;
        }

        sawExtension = true;
        if (!policy.Strict) return true;
        if (policy.KnownExtensionId is not null && !policy.KnownExtensionId(typeId)) return false;
        extensionIds ??= new List<ushort>(4);
        if (extensionIds.Contains(typeId)) return false;
        extensionIds.Add(typeId);
        return true;
    }
}
