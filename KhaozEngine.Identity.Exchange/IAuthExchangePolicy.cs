using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;

namespace KhaozEngine.Identity.Exchange;

/// <summary>
/// What a game does differently ON PURPOSE around the fixed exchange: the name it stores, and the claims it signs
/// into the token. Both members have defaults, so a game that wants neither passes no policy at all.
/// </summary>
/// <remarks>
/// The gate itself is not here and cannot be. <see cref="AuthAdmission.Decide"/> refuses a banned account before an
/// unwhitelisted one, because that order is a disclosure property (a banned player never learns whether they were
/// whitelisted), so a policy adds claims after admission and never reorders it. A member that throws, or returns a
/// value the exchange cannot use, answers <see cref="AuthExchangeOutcome.Unavailable"/> with
/// <see cref="AuthExchangeCause.PolicyFault"/>.
/// </remarks>
public interface IAuthExchangePolicy
{
    /// <summary>
    /// The display name handed to the store for a verified identity, before the exchange clamps it to
    /// <see cref="AuthExchangeOptions.MaxDisplayNameChars"/>. <c>null</c> means none, which on a repeat sign-in keeps
    /// the stored name. The default is the provider's name, else the provider subject, so a nameplate never reads
    /// empty. A game that stores <c>null</c> when the provider gives no name returns
    /// <see cref="VerifiedIdentity.DisplayName"/> unchanged.
    /// </summary>
    /// <param name="identity">The identity the validator verified.</param>
    string? ResolveDisplayName(VerifiedIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.Subject : identity.DisplayName;

    /// <summary>
    /// The claims signed into the token of an ADMITTED account. Runs only after <see cref="AuthAdmission.Decide"/>
    /// admitted it, so a game may create per-account state here (a character shell, say) without creating it for a
    /// banned or unwhitelisted caller. The default is the stored name, else the subject, and no persistence key,
    /// which mints a v2 token.
    /// </summary>
    /// <param name="account">The admitted account, as the store returned it.</param>
    /// <param name="ct">The caller's cancellation.</param>
    ValueTask<SessionClaims> IssueClaimsAsync(AccountRecord account, CancellationToken ct) =>
        ValueTask.FromResult(new SessionClaims(account.DisplayName ?? account.Subject, PersistenceKey: null));
}

/// <summary>The claims one token carries beside the verified subject.</summary>
/// <param name="DisplayName">The cosmetic name signed into the token. Never null. Empty is allowed and signs an empty
/// name claim.</param>
/// <param name="PersistenceKey">A durable identity hint for the game server, or <c>null</c> (or empty) for none. Present,
/// the exchange mints a v3 token carrying it. Absent, it mints v2.</param>
public readonly record struct SessionClaims(string DisplayName, string? PersistenceKey);
