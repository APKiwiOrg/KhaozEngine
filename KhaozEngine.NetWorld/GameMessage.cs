using System;

namespace KhaozEngine.NetWorld;

/// <summary>Handles a client-to-server game message on the host thread during <c>Poll</c>: the sending player's
/// <paramref name="slot"/>, the game-defined <paramref name="kind"/> discriminator, and the opaque
/// <paramref name="payload"/>. The engine never interprets <paramref name="payload"/> - it is raw bytes the game
/// serialized (an attack, an interaction, a chat line, an inventory transaction, …). A ref-struct span so raising the
/// event allocates nothing; if a handler needs to keep the bytes past the call it must copy them
/// (<c>payload.ToArray()</c>). Fired by <see cref="WorldServer.OnGameMessage"/> /
/// <see cref="ShardedWorldServer.OnGameMessage"/>.</summary>
public delegate void ServerGameMessageHandler(int slot, ushort kind, ReadOnlySpan<byte> payload);

/// <summary>Handles a server-to-client game message on the client thread during <c>Poll</c>: the game-defined
/// <paramref name="kind"/> discriminator and the opaque <paramref name="payload"/> (the engine never interprets it).
/// A ref-struct span so raising the event allocates nothing; a handler that keeps the bytes must copy them
/// (<c>payload.ToArray()</c>). Fired by <see cref="WorldClient.GameMessageReceived"/>.</summary>
public delegate void ClientGameMessageHandler(ushort kind, ReadOnlySpan<byte> payload);
