namespace KhaozEngine.Identity.Exchange;

/// <summary>
/// The six answers an exchange can give. Each maps to one wire status and one HTTP status, and the mapping is what
/// keeps the endpoint from being an account oracle: see <see cref="AuthExchangeResult.ToResponse"/>.
/// </summary>
public enum AuthExchangeOutcome
{
    /// <summary>The credential verified and the account is admitted. A token was minted. HTTP 200.</summary>
    Ok = 0,

    /// <summary>The caller's own account is not banned but is not whitelisted. HTTP 403.</summary>
    NotWhitelisted = 1,

    /// <summary>The caller's own account has a ban in force. Decided before the whitelist. HTTP 403.</summary>
    Banned = 2,

    /// <summary>The provider answered and refused the credential. No account was touched. HTTP 401.</summary>
    InvalidCredential = 3,

    /// <summary>
    /// A dependency could not answer: the provider reported an outage or a rate limit, missed its deadline, or the
    /// store or the policy failed. Nothing about the account was decided. HTTP 503, whichever dependency it was.
    /// </summary>
    Unavailable = 4,

    /// <summary>
    /// The request itself was unusable: an unknown provider id, or a blank or oversized credential. Decided before
    /// any provider call. HTTP 400 with no body.
    /// </summary>
    Malformed = 5,
}

/// <summary>
/// Why an exchange ended as it did, for the host's log line. Never serialized and never sent: every
/// <see cref="AuthExchangeOutcome.Unavailable"/> cause answers the same envelope, so the response does not say
/// which dependency failed. An enum carries no account data, so it is always safe to log.
/// </summary>
public enum AuthExchangeCause
{
    /// <summary>A decision about an admitted, banned or unwhitelisted account. Nothing failed.</summary>
    None = 0,

    /// <summary><see cref="AuthExchangeOutcome.Malformed"/>: no validator is registered under the provider id.</summary>
    UnknownProvider = 1,

    /// <summary><see cref="AuthExchangeOutcome.Malformed"/>: the credential is blank, over the cap, or carries anything
    /// but visible ASCII.</summary>
    CredentialShape = 2,

    /// <summary><see cref="AuthExchangeOutcome.InvalidCredential"/>: the provider refused the credential.</summary>
    CredentialRefused = 3,

    /// <summary>
    /// <see cref="AuthExchangeOutcome.Unavailable"/>: the provider reported an outage or a rate limit, or the
    /// validator threw. A provider 5xx or 429 lands here, never on a refused credential.
    /// </summary>
    ProviderUnavailable = 4,

    /// <summary><see cref="AuthExchangeOutcome.Unavailable"/>: the provider did not answer within the deadline.</summary>
    ProviderTimeout = 5,

    /// <summary><see cref="AuthExchangeOutcome.Unavailable"/>: the account store threw or returned nothing.</summary>
    StoreFault = 6,

    /// <summary>
    /// <see cref="AuthExchangeOutcome.Unavailable"/>: the store returned a subject a <c>SignedToken</c> or the join
    /// gate refuses (empty, carrying a <c>.</c>, or under the reserved <c>guest:</c> prefix). A server fault, and
    /// no token is minted for it.
    /// </summary>
    InadmissibleSubject = 7,

    /// <summary><see cref="AuthExchangeOutcome.Unavailable"/>: the game's policy threw or returned an unusable value.</summary>
    PolicyFault = 8,
}
