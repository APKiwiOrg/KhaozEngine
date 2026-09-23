using System;
using System.Text;
using System.Text.Json.Serialization;

namespace KhaozEngine.Identity;

/// <summary>
/// Wire request to <c>POST /auth/exchange</c>: a provider credential the auth service verifies and trades for a
/// session token. The body is <c>{"provider":"discord","accessToken":"..."}</c>.
/// </summary>
/// <remarks>
/// The JSON names are pinned on the type, so the wire does not depend on the serializer options a caller happens to
/// use. They are the names both games' clients and auth services exchange today. <see cref="ToString"/> never prints
/// the credential.
/// </remarks>
/// <param name="Provider">The identity provider id, for example <c>discord</c>. Matched ordinally against the
/// validator's <see cref="IIdentityValidator.ProviderId"/>.</param>
/// <param name="AccessToken">The provider credential. A bearer secret: never log it.</param>
public sealed record AuthExchangeRequest(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("accessToken")] string AccessToken)
{
    // The record's generated ToString would print the credential into any log that formats the request.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Provider = ").Append(Provider);
        return true;
    }
}

/// <summary>
/// Wire response from <c>POST /auth/exchange</c>. <see cref="Status"/> is one of <see cref="AuthExchangeStatuses"/>,
/// and everything after it is <c>null</c> unless the exchange got far enough to know it: a refused credential names
/// no subject at all.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the union of the two games' records, so a client of either reads it with no wire change. Every
/// member is written, <c>null</c> included, which is byte for byte what the web-default serializer writes for these
/// records today. A reader skips a member it does not know, so a client that predates the ban fields reads this
/// response unchanged, and this record reads a response carrying a member it lacks.
/// </para>
/// <para>
/// The ban fields are set only on <see cref="AuthExchangeStatuses.Banned"/>, and only when the service opts in to
/// ban details. <see cref="ToString"/> never prints the session token, the subject or the display name.
/// </para>
/// </remarks>
/// <param name="Status">Which answer this is, one of <see cref="AuthExchangeStatuses"/>.</param>
/// <param name="SessionToken">The minted token, on <see cref="AuthExchangeStatuses.Ok"/> only. A bearer secret.</param>
/// <param name="ExpiresAtUtc">When that token stops verifying.</param>
/// <param name="Subject">The verified account subject, once the credential was good enough to name one.</param>
/// <param name="DisplayName">The account's stored display name, or <c>null</c> when it has none.</param>
/// <param name="BanReason">The filed ban's reason, on <see cref="AuthExchangeStatuses.Banned"/> with ban details
/// enabled only.</param>
/// <param name="BanExpiresAtUtc">When the ban lapses, <c>null</c> for a permanent ban or when ban details are
/// off.</param>
public sealed record AuthExchangeResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sessionToken")] string? SessionToken = null,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("banReason")] string? BanReason = null,
    [property: JsonPropertyName("banExpiresAtUtc")] DateTimeOffset? BanExpiresAtUtc = null)
{
    // The record's generated ToString would print the session token and the account into any log that formats it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Status = ").Append(Status);
        return true;
    }
}

/// <summary>
/// The <see cref="AuthExchangeResponse.Status"/> values. Wire tokens, not player-facing text: a client maps each to
/// a localized string and never displays one.
/// </summary>
public static class AuthExchangeStatuses
{
    /// <summary>The credential verified and the account may play. A session token came with it.</summary>
    public const string Ok = "ok";

    /// <summary>The account is not banned but is not on the whitelist.</summary>
    public const string NotWhitelisted = "not_whitelisted";

    /// <summary>The account is banned. Decided before the whitelist, so a ban reveals nothing else.</summary>
    public const string Banned = "banned";

    /// <summary>The provider refused the credential, so no account was looked up. Sign in again.</summary>
    public const string InvalidCredential = "invalid_credential";

    /// <summary>
    /// A dependency could not answer (the provider, its deadline, the account store). Nothing about the account
    /// was decided, so keep the credential and retry later.
    /// </summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Client-synthesized, never sent. The service answers a rate-limited request with a bare 429 and no body, and a
    /// client turns that status into this, so a player told to wait is not told the service is down.
    /// </summary>
    public const string RetryLater = "retry_later";
}
