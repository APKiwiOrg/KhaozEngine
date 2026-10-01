using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Puts the chunks of a <see cref="TileFragmentedMessage"/> back together for ONE peer: a
/// <see cref="MessageReassembler"/> at <see cref="TileFragmentedMessage.MaxChunkPayloadBytes"/>. The four rules,
/// the refusal tokens and the memory bound are the core type's.
/// <para>Each chunk is the payload of a game message the caller has already unwrapped with
/// <see cref="TileProtocol.TryDecodeGameMessage"/>, sent on the <c>ReliableOrdered</c> channel. One reassembler
/// per connection slot, dropped through <see cref="DropConnection"/> from the server's disconnect path.</para>
/// </summary>
public sealed class TileFragmentReassembler
{
    /// <inheritdoc cref="MessageReassembler.MaxPartialAssemblies"/>
    public const int MaxPartialAssemblies = MessageReassembler.MaxPartialAssemblies;

    /// <inheritdoc cref="MessageReassembler.MalformedChunk"/>
    public const string MalformedChunk = MessageReassembler.MalformedChunk;

    /// <inheritdoc cref="MessageReassembler.OutOfSequenceChunk"/>
    public const string OutOfSequenceChunk = MessageReassembler.OutOfSequenceChunk;

    readonly MessageReassembler core;

    /// <summary>Builds a reassembler for one connection slot at the tile chunk width.</summary>
    public TileFragmentReassembler(int slot) =>
        core = new MessageReassembler(slot, TileFragmentedMessage.MaxChunkPayloadBytes);

    /// <inheritdoc cref="MessageReassembler.Slot"/>
    public int Slot => core.Slot;

    /// <inheritdoc cref="MessageReassembler.EvictedAssemblies"/>
    public int EvictedAssemblies => core.EvictedAssemblies;

    /// <inheritdoc cref="MessageReassembler.PartialAssemblyCount"/>
    public int PartialAssemblyCount => core.PartialAssemblyCount;

    /// <inheritdoc cref="MessageReassembler.TryComplete(ReadOnlySpan{byte}, out ReadOnlyMemory{byte}, out string?)"/>
    public bool TryComplete(ReadOnlySpan<byte> chunk, out ReadOnlyMemory<byte> assembled, out string? reason) =>
        core.TryComplete(chunk, out assembled, out reason);

    /// <inheritdoc cref="MessageReassembler.TryComplete(ReadOnlySpan{byte}, out byte, out ReadOnlyMemory{byte}, out string?)"/>
    public bool TryComplete(ReadOnlySpan<byte> chunk, out byte streamId, out ReadOnlyMemory<byte> assembled,
        out string? reason) =>
        core.TryComplete(chunk, out streamId, out assembled, out reason);

    /// <inheritdoc cref="MessageReassembler.DropConnection"/>
    public bool DropConnection(int slot) => core.DropConnection(slot);
}
