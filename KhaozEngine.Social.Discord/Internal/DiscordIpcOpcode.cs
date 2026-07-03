namespace KhaozEngine.Social.Discord.Internal;

/// <summary>Discord IPC frame opcodes (little-endian 4-byte header field).</summary>
internal enum DiscordIpcOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}
