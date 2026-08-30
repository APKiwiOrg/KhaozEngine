using System;

namespace KhaozEngine.NetWorld;

/// <summary>The kind of out-of-band notice a server pushes to connected clients. <see cref="Shutdown"/> also lets a
/// client attribute a following drop to <c>DisconnectReason.ServerShutdown</c> (a planned restart, not a crash), and
/// <see cref="Banned"/> does the same for <c>DisconnectReason.Banned</c>.
/// <see cref="Banned"/> is the typed rejection sent just before a banned account is dropped: it carries no
/// engine-authored display text, so the client maps the kind to its own localized string (a headless server owns no
/// string catalog). Adding a value is wire-additive: the byte kind is written/read raw with no validation (see
/// <see cref="MoveProtocol.EncodeNotice"/> / <see cref="MoveProtocol.DecodeNotice"/>), so an older client just treats
/// an unknown kind as opaque.</summary>
public enum ServerNoticeKind : byte { Custom = 0, Maintenance = 1, Shutdown = 2, Banned = 3 }

/// <summary>
/// A small typed message broadcast server->client and surfaced on <see cref="WorldClient"/> (event + latest
/// property) for the consumer to display, e.g. a maintenance/restart warning. Common cases are first-class
/// (<see cref="Kind"/> + <see cref="Message"/> + an optional <see cref="SecondsUntil"/> countdown); a
/// <see cref="ServerNoticeKind.Custom"/> notice may also carry an opaque <see cref="Payload"/> the game decodes.
/// </summary>
public readonly struct ServerNotice
{
    public ServerNotice(ServerNoticeKind kind, string message, float? secondsUntil = null, byte[]? payload = null)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        SecondsUntil = secondsUntil;
        Payload = payload ?? Array.Empty<byte>();
    }

    /// <summary>What the notice is about.</summary>
    public ServerNoticeKind Kind { get; }

    /// <summary>Human-readable text (capped at <see cref="MoveProtocol.MaxNoticeMessageBytes"/> on the wire).</summary>
    public string Message { get; }

    /// <summary>Optional countdown in seconds (e.g. "restarting in N s"); null when not applicable.</summary>
    public float? SecondsUntil { get; }

    /// <summary>Opaque game-defined bytes for a <see cref="ServerNoticeKind.Custom"/> notice (capped at
    /// <see cref="MoveProtocol.MaxNoticePayloadBytes"/>); empty for the typed kinds.</summary>
    public byte[] Payload { get; }
}
