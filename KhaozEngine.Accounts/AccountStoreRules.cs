using System;

namespace KhaozEngine.Accounts;

/// <summary>
/// The rules every ENGINE account store applies, written once so the in-memory reference store and the durable
/// backends mint, refuse and bound identically. A game's own <see cref="IAccountStore"/> over its own schema is
/// free to mint differently, and <see cref="IsAdmissibleSubject"/> is the part of the rule any store's output
/// must still meet.
/// </summary>
/// <remarks>
/// <para>
/// <b>The subject is <c>{ProviderId}:{ProviderSubject}</c></b>, Grimhollow's <c>discord:&lt;snowflake&gt;</c>. The
/// provider id carries no <see cref="SubjectSeparator"/>, so the subject splits unambiguously at its first one.
/// Nothing carries a <c>.</c>, because <c>SignedToken</c> splits its fields on it. And no subject lands under
/// <see cref="ReservedSubjectPrefix"/>, which names a tokenless connection's seat: persistence refuses to file a
/// record under it and the join gate refuses a verified subject carrying it.
/// </para>
/// <para>
/// <b>The length limits are the engine table's columns</b> (<c>subject</c> NVARCHAR(128), <c>display_name</c>
/// NVARCHAR(128), <c>ban_reason</c> NVARCHAR(256) on SQL Server), counted in UTF-16 code units, which is what
/// <see cref="string.Length"/> counts. Applying them in every engine store turns a value one backend would
/// truncate or fail on into the same <see cref="ArgumentException"/> everywhere, before anything is written.
/// </para>
/// <para>
/// Every refusal names the rule and never the value: a provider subject, a display name and a ban reason are
/// account data, and a message is something a host logs.
/// </para>
/// </remarks>
public static class AccountStoreRules
{
    /// <summary>Joins the provider id and the provider subject in a minted subject.</summary>
    public const char SubjectSeparator = ':';

    /// <summary>The longest subject an engine store mints or holds, in UTF-16 code units.</summary>
    public const int MaxSubjectChars = 128;

    /// <summary>The longest display name an engine store holds, in UTF-16 code units.</summary>
    public const int MaxDisplayNameChars = 128;

    /// <summary>The longest ban reason an engine store holds, in UTF-16 code units.</summary>
    public const int MaxBanReasonChars = 256;

    /// <summary>
    /// The subject prefix reserved for a tokenless connection's seat (<c>guest:{slot}</c>). The same literal as
    /// <c>KhaozEngine.WorldStore.PositionHintCache.GuestAccountPrefix</c>, which this package cannot reference, and
    /// a parity test holds the two equal. Ordinal: <c>Guest:</c> and a bare <c>guest</c> are ordinary subjects.
    /// </summary>
    public const string ReservedSubjectPrefix = "guest:";

    /// <summary>
    /// Mints the subject for <paramref name="signIn"/> and validates every field an engine store keeps from it.
    /// </summary>
    /// <param name="signIn">The verified sign-in.</param>
    /// <returns><c>{ProviderId}:{ProviderSubject}</c>.</returns>
    /// <exception cref="ArgumentException">The provider id is blank or carries a <c>:</c> or a <c>.</c>, the provider
    /// subject is blank or carries a <c>.</c>, the subject would fall under <see cref="ReservedSubjectPrefix"/> or
    /// exceed <see cref="MaxSubjectChars"/>, or the display name exceeds <see cref="MaxDisplayNameChars"/>.</exception>
    public static string MintSubject(AccountSignIn signIn)
    {
        string? providerId = signIn.ProviderId;
        string? providerSubject = signIn.ProviderSubject;

        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("The sign-in has no provider id.", nameof(signIn));
        if (providerId.Contains(SubjectSeparator) || providerId.Contains('.'))
            throw new ArgumentException(
                $"A provider id may not contain '{SubjectSeparator}' or '.', which would make the minted subject " +
                "ambiguous or unable to ride a SignedToken.", nameof(signIn));
        if (string.IsNullOrWhiteSpace(providerSubject))
            throw new ArgumentException("The sign-in has no provider subject.", nameof(signIn));
        if (HasSurroundingWhitespace(providerId) || HasSurroundingWhitespace(providerSubject))
            throw new ArgumentException(
                "A provider id or subject may not begin or end with whitespace. SQL Server compares padded strings, so " +
                "'x' and 'x ' would collide on one account.", nameof(signIn));
        if (providerSubject.Contains('.'))
            throw new ArgumentException(
                "A provider subject containing '.' cannot ride a SignedToken, whose fields are split on it. The engine " +
                "stores refuse it until a store mints a surrogate id instead.", nameof(signIn));

        string subject = string.Concat(providerId, SubjectSeparator.ToString(), providerSubject);
        if (subject.StartsWith(ReservedSubjectPrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"A subject under the reserved '{ReservedSubjectPrefix}' prefix names a tokenless seat, so it is " +
                "never minted. Register the validator under a different provider id.", nameof(signIn));
        if (subject.Length > MaxSubjectChars)
            throw new ArgumentException(
                $"The minted subject would be {subject.Length} characters, over the {MaxSubjectChars} an engine " +
                "store holds.", nameof(signIn));
        if (signIn.DisplayName is { Length: > MaxDisplayNameChars } name)
            throw new ArgumentException(
                $"The display name is {name.Length} characters, over the {MaxDisplayNameChars} an engine store " +
                "holds. Clamp it before the store sees it.", nameof(signIn));

        return subject;
    }

    /// <summary>Validates a ban reason an engine store is asked to keep.</summary>
    /// <param name="reason">The reason. Empty is allowed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> exceeds <see cref="MaxBanReasonChars"/>.</exception>
    public static void ValidateBanReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Length > MaxBanReasonChars)
            throw new ArgumentException(
                $"The ban reason is {reason.Length} characters, over the {MaxBanReasonChars} an engine store holds.",
                nameof(reason));
    }

    /// <summary>
    /// Whether <paramref name="subject"/> is one the rest of the engine accepts as a verified account id: non-empty,
    /// no <c>.</c> (so a <c>SignedToken</c> can carry it) and not under <see cref="ReservedSubjectPrefix"/> (so the
    /// join gate admits it and persistence files it). Every subject an engine store mints meets it. A caller holding
    /// a subject from any other store checks it here.
    /// </summary>
    public static bool IsAdmissibleSubject(string? subject) =>
        !string.IsNullOrEmpty(subject)
        && !HasSurroundingWhitespace(subject)
        && !subject.Contains('.')
        && !subject.StartsWith(ReservedSubjectPrefix, StringComparison.Ordinal);

    private static bool HasSurroundingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
}
