using System;
using System.Text;
using KhaozEngine.Accounts;

namespace KhaozEngine.Identity.Exchange;

/// <summary>
/// What one exchange decided. No HTTP type: the host maps <see cref="Outcome"/> to a status code and
/// <see cref="ToResponse"/> to the body.
/// </summary>
/// <remarks>
/// <para>
/// Everything after <see cref="Outcome"/> is <c>null</c> unless the exchange got far enough to know it. The account
/// fields are set only on <see cref="AuthExchangeOutcome.Ok"/>, <see cref="AuthExchangeOutcome.NotWhitelisted"/> and
/// <see cref="AuthExchangeOutcome.Banned"/>, each of which follows a verified credential and describes the caller's
/// OWN account, which the store found or created. So no result ever means "no such account".
/// </para>
/// <para>
/// <see cref="Cause"/> and <see cref="Fault"/> are for the host's log and are never serialized. A fault from a game's
/// own store may quote account data in its message, so log it where account data may go. <see cref="ToString"/>
/// prints only the outcome and the cause, never the token, the subject or the display name.
/// </para>
/// </remarks>
/// <param name="Outcome">Which answer this is.</param>
/// <param name="SessionToken">The minted <c>SignedToken</c>, on <see cref="AuthExchangeOutcome.Ok"/> only. A bearer
/// secret: never log it.</param>
/// <param name="ExpiresAtUtc">When that token stops verifying: the clock at the exchange plus the lifetime.</param>
/// <param name="Subject">The account subject the store minted.</param>
/// <param name="DisplayName">The account's stored display name, or <c>null</c> when it has none.</param>
/// <param name="Ban">The ban in force, on <see cref="AuthExchangeOutcome.Banned"/> only. Sent to the client only when
/// the host opts in to ban details.</param>
/// <param name="Fault">What failed, on <see cref="AuthExchangeOutcome.Unavailable"/> when an exception is the
/// explanation. For the host's log, never serialized.</param>
/// <param name="Cause">Why the exchange ended as it did, for the host's log. Never serialized.</param>
public sealed record AuthExchangeResult(
    AuthExchangeOutcome Outcome, string? SessionToken = null, DateTimeOffset? ExpiresAtUtc = null,
    string? Subject = null, string? DisplayName = null, AccountBan? Ban = null,
    Exception? Fault = null, AuthExchangeCause Cause = AuthExchangeCause.None)
{
    /// <summary>
    /// The wire body for this result. The host sends it with the status code its <see cref="Outcome"/> maps to:
    /// 200 <c>ok</c>, 403 <c>not_whitelisted</c>, 403 <c>banned</c>, 401 <c>invalid_credential</c> and 503
    /// <c>unavailable</c>. <see cref="AuthExchangeOutcome.Malformed"/> is a bare 400 with no body, so it has none.
    /// </summary>
    /// <remarks>
    /// Only what the outcome is entitled to reaches the body. <c>ok</c> carries the token, its expiry, the subject and
    /// the display name. <c>not_whitelisted</c> and <c>banned</c> carry the subject and the display name, and
    /// <c>banned</c> adds the ban's reason and expiry only when <paramref name="includeBanDetails"/> is set.
    /// <c>invalid_credential</c> and <c>unavailable</c> carry the status alone, so every provider refusal reads alike,
    /// and every dependency failure reads alike whatever failed and whatever the account's standing.
    /// </remarks>
    /// <param name="includeBanDetails">Whether a banned caller is told the reason and the expiry. Off unless the game
    /// opts in, so a ban reason written for operators does not reach a player by default.</param>
    /// <exception cref="InvalidOperationException"><see cref="Outcome"/> is <see cref="AuthExchangeOutcome.Malformed"/>
    /// or not a defined outcome.</exception>
    public AuthExchangeResponse ToResponse(bool includeBanDetails = false) => Outcome switch
    {
        AuthExchangeOutcome.Ok => new AuthExchangeResponse(
            AuthExchangeStatuses.Ok, SessionToken, ExpiresAtUtc, Subject, DisplayName),
        AuthExchangeOutcome.NotWhitelisted => new AuthExchangeResponse(
            AuthExchangeStatuses.NotWhitelisted, Subject: Subject, DisplayName: DisplayName),
        AuthExchangeOutcome.Banned => new AuthExchangeResponse(
            AuthExchangeStatuses.Banned, Subject: Subject, DisplayName: DisplayName,
            BanReason: includeBanDetails ? Ban?.Reason : null,
            BanExpiresAtUtc: includeBanDetails ? Ban?.Until : null),
        AuthExchangeOutcome.InvalidCredential => new AuthExchangeResponse(AuthExchangeStatuses.InvalidCredential),
        AuthExchangeOutcome.Unavailable => new AuthExchangeResponse(AuthExchangeStatuses.Unavailable),
        AuthExchangeOutcome.Malformed => throw new InvalidOperationException(
            "A malformed exchange answers a bare 400 with no body, so it has no response envelope."),
        _ => throw new InvalidOperationException($"{Outcome} is not a defined exchange outcome."),
    };

    // The record's generated ToString would print the token, the subject and the display name into a log line.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Outcome = ").Append(Outcome).Append(", Cause = ").Append(Cause);
        return true;
    }
}
