using System;
using System.Collections.Generic;
using KhaozEngine.Identity;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class CoreTypesTests
{
    [Fact]
    public void ProviderCredential_carries_its_fields()
    {
        DateTimeOffset exp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ProviderCredential c = new("oidc", "tok", "refresh", exp);
        Assert.Equal("oidc", c.ProviderId);
        Assert.Equal("tok", c.CredentialToken);
        Assert.Equal("refresh", c.RefreshToken);
        Assert.Equal(exp, c.ExpiresAtUtc);
    }

    [Fact]
    public void VerifiedIdentity_carries_subject_and_claims()
    {
        VerifiedIdentity v = new("sub-123", "discord", "Nick", new Dictionary<string, string> { ["email"] = "a@b.c" });
        Assert.Equal("sub-123", v.Subject);
        Assert.Equal("Nick", v.DisplayName);
        Assert.Equal("a@b.c", v.Claims["email"]);
    }
}
