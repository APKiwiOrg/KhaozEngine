using System;
using KhaozEngine.Identity;
using Xunit;

namespace KhaozEngine.Tests.Identity;

/// <summary>The absence check for a sign-in refusal. Exception.ToString covers the message and the inner chain but
/// leaves out custom properties, so each string identifier is read here too.</summary>
internal static class SignInRefusalSecrets
{
    public static void AssertCarriesNone(SignInException refusal, params string[] secrets)
    {
        string carried = string.Join('\n', refusal.ToString(), refusal.Detail, refusal.ProviderId, refusal.MemberName,
            refusal.ActualScheme);
        foreach (string secret in secrets)
            Assert.DoesNotContain(secret, carried, StringComparison.Ordinal);
    }
}
