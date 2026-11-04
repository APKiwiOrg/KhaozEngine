using System;
using System.IO;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The cell-blob rewrite for the one built-in that SHRANK: a <see cref="MovementState"/> payload stored before
/// <see cref="BuiltinBlobLayout.MovementOwnerWireGeneration"/> carries the two feel timers that now live on
/// <see cref="MovementOwnerState"/>. Bringing it forward is a split, not a pad. The movement payload is re-emitted
/// without the timers (then transformed by MovementPayloadRewrite), and the eight timer bytes become a
/// <see cref="MoveProtocol.MovementOwnerTypeId"/> frame of their own, so a restored player keeps its coyote and
/// jump-buffer windows exactly as the stored body had them.
/// <para>The owner frame is emitted where the live writer would put it. Built-in frames go out in registration order
/// and the owner id is the last built-in registered, so it follows every other built-in frame of the entity and
/// precedes its first extension frame and its terminator (<see cref="ShouldFlushBefore"/>).</para>
/// </summary>
internal static class MovementOwnerBlobSplit
{
    /// <summary>Whether a movement payload walked at <paramref name="fromGeneration"/> has to be split to reach
    /// <paramref name="toGeneration"/>.</summary>
    internal static bool Applies(int fromGeneration, int toGeneration) =>
        fromGeneration < BuiltinBlobLayout.MovementOwnerWireGeneration
        && toGeneration >= BuiltinBlobLayout.MovementOwnerWireGeneration;

    /// <summary>Removes the two timers into a separate owner payload. The caller supplies a complete historical
    /// movement payload and enough scratch space, then applies subsequent movement transformations.</summary>
    internal static int RemoveTimers(ReadOnlySpan<byte> source, Span<byte> destination, out byte[] timers)
    {
        int head = BuiltinBlobLayout.MovementTimersOffset;
        int cut = BuiltinBlobLayout.MovementOwnerPayloadBytes;
        timers = source.Slice(head, cut).ToArray();
        source[..head].CopyTo(destination);
        source[(head + cut)..].CopyTo(destination[head..]);
        return source.Length - cut;
    }

    /// <summary>Whether a held owner frame must be written before the frame whose id is <paramref name="typeId"/>
    /// (0 is the entity terminator): true for the terminator, for an extension frame, and for any built-in id
    /// registered after the owner id.</summary>
    internal static bool ShouldFlushBefore(ushort typeId) =>
        typeId == 0 || typeId > MoveProtocol.MovementOwnerTypeId;

    /// <summary>Writes <c>[MovementOwnerTypeId][timers]</c>, the frame the live codec writes for a
    /// <see cref="MovementOwnerState"/>.</summary>
    internal static void WriteOwnerFrame(BinaryWriter bw, byte[] timers)
    {
        ArgumentNullException.ThrowIfNull(timers);
        bw.Write(MoveProtocol.MovementOwnerTypeId);
        bw.Write(timers);
    }
}
