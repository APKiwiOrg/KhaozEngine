using System;
using System.Collections.Generic;

namespace KhaozEngine.Accounts;

/// <summary>
/// What a VERIFIED sign-in hands the store. Everything here was established by an identity validator, never
/// asserted by a client.
/// </summary>
/// <remarks>
/// The STORE mints the subject from it, not the caller. A game adapter over its own schema may mint a
/// provider-neutral id from an identity column (<c>acct:&lt;id&gt;</c>) and link provider logins beside it. The
/// engine stores mint <c>{ProviderId}:{ProviderSubject}</c> through <see cref="AccountStoreRules.MintSubject"/>.
/// </remarks>
/// <param name="ProviderId">The validator's provider id, for example <c>discord</c>.</param>
/// <param name="ProviderSubject">The subject the provider verified, for example a Discord snowflake.</param>
/// <param name="DisplayName">The name to store, as the caller's policy resolved it, or <c>null</c> when there is none.
/// A <c>null</c> on a repeat sign-in keeps the stored name rather than clearing it.</param>
/// <param name="Claims">Further verified claims (an email, say) for a game adapter that keeps them. The engine stores
/// keep none.</param>
/// <param name="At">When the sign-in was verified, for an adapter that records a last-seen time. The engine stores
/// keep none.</param>
public readonly record struct AccountSignIn(
    string ProviderId, string ProviderSubject, string? DisplayName,
    IReadOnlyDictionary<string, string> Claims, DateTimeOffset At);
