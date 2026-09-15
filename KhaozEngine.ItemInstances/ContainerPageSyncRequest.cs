using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Spec 7.6's one new client-to-server message, and it carries two bytes: "send me this page of this
/// container". It is what <see cref="ContainerPageDelta"/> leans on, because a client that cannot apply a
/// delta asks for the page rather than guessing at it, and it is what a client receiving a journal
/// correction's section key list sends for each page the correction named.
/// <code>
/// [ContainerId: byte][PageIndex: byte]
/// </code>
/// <para>
/// <b>It names things by id and carries nothing else</b>, which is spec 7.6's invariant and spec 15.3 in
/// practice: no client-to-server message in this design carries an instance payload, and every one of them
/// names an item by id. There is no field here a payload could ride in, which is what makes "craft a payload,
/// send it, get the item" unreachable rather than mitigated.
/// </para>
/// <para>
/// <b>The server rate limits this at ONE PAGE PER CLIENT PER TICK.</b> That is a documented server rule
/// rather than engine code here, because the engine caps the frame and the game owns the message kinds and
/// the tick, so there is nothing in this package to enforce it from. It bounds the worst case a malicious
/// client can ask for at one page of fragments per tick, which is the same shape the snapshot already costs.
/// A server that serves every request it receives has handed an unauthenticated peer an amplifier: two bytes
/// in, about 7 KB out, for as long as it cares to ask. The package README says this too.
/// </para>
/// <para>
/// <b>The three client rules this request completes.</b> A client REFUSES a delta for a page it has not fully
/// received, and sends this instead, so a delta is never applied to bytes the client guessed at. On the last
/// chunk of a fragmented page the assembled bytes go through the SAME decoder the server encoded with,
/// <see cref="ItemContainerPageCodec.TryDecode"/>. A page that fails to decode quarantines rather than
/// throwing, and asking again is the recovery: a client that trusted its own reassembly would draw a bank
/// from bytes nothing validated.
/// </para>
/// </summary>
/// <param name="ContainerId">Which container, the GAME's number for it. Opaque to the engine, exactly as the
/// delta's is.</param>
/// <param name="PageIndex">Which page of that container, spec 5.2's geometry.</param>
public readonly record struct ContainerPageSyncRequest(byte ContainerId, byte PageIndex)
{
    /// <summary>The wire size, in bytes. Fixed, because both fields are bytes.</summary>
    public const int Bytes = 2;

    /// <summary>Writes the request and answers <see cref="Bytes"/>.</summary>
    /// <param name="destination">At least <see cref="Bytes"/> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short, which is a caller bug
    /// on the SENDING side and so throws, unlike <see cref="TryRead"/>.</exception>
    public int Write(Span<byte> destination)
    {
        if (destination.Length < Bytes)
            throw new ArgumentException($"a page sync request needs {Bytes} bytes", nameof(destination));

        destination[0] = ContainerId;
        destination[1] = PageIndex;
        return Bytes;
    }

    /// <summary>The request as a fresh array, for a caller holding a message payload rather than a buffer.</summary>
    public byte[] ToArray() => [ContainerId, PageIndex];

    /// <summary>
    /// Reads a request off the wire. NEVER throws, on the rule every remote decoder in this design follows:
    /// these bytes came from a client, so a malformed one is a false and a dropped message rather than an
    /// exception out of the receive loop.
    /// <para>False for anything that is not EXACTLY <see cref="Bytes"/> bytes. Trailing bytes on a fixed two
    /// byte message are a frame this encoder never wrote, and reading it anyway would accept a peer probing
    /// for a reader that guesses.</para>
    /// </summary>
    /// <param name="bytes">The message payload.</param>
    /// <param name="request">The request, or the default value when this answers false.</param>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out ContainerPageSyncRequest request)
    {
        if (bytes.Length != Bytes)
        {
            request = default;
            return false;
        }

        request = new ContainerPageSyncRequest(bytes[0], bytes[1]);
        return true;
    }
}
