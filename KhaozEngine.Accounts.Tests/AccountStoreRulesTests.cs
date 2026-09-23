using System;
using System.Collections.Generic;
using KhaozEngine.Accounts;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>The subject rule and the limits, as pure functions, and the one constant restated from another package.</summary>
public class AccountStoreRulesTests
{
    private static AccountSignIn SignIn(string provider, string providerSubject, string? displayName = null) =>
        new(provider, providerSubject, displayName, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void ReservedSubjectPrefix_IsTheSeatPrefixPersistenceRefuses() =>
        Assert.Equal(PositionHintCache.GuestAccountPrefix, AccountStoreRules.ReservedSubjectPrefix);

    [Fact]
    public void MintSubject_IsProviderColonSubject()
    {
        Assert.Equal("discord:80351110224678912", AccountStoreRules.MintSubject(SignIn("discord", "80351110224678912")));
        // The provider id carries no colon, so a provider subject may, and the subject still splits at the first.
        Assert.Equal("oidc:tenant:user-7", AccountStoreRules.MintSubject(SignIn("oidc", "tenant:user-7")));
    }

    [Fact]
    public void MintSubject_KeepsThePrefixRuleOrdinal()
    {
        Assert.Equal("Guest:1", AccountStoreRules.MintSubject(SignIn("Guest", "1")));
        Assert.Equal("guests:1", AccountStoreRules.MintSubject(SignIn("guests", "1")));
        Assert.Throws<ArgumentException>(() => AccountStoreRules.MintSubject(SignIn("guest", "1")));
    }

    [Fact]
    public void MintSubject_AcceptsTheLimits_AndRefusesOneOver()
    {
        string atLimit = new('7', AccountStoreRules.MaxSubjectChars - "discord:".Length);
        string name = new('n', AccountStoreRules.MaxDisplayNameChars);

        Assert.Equal(AccountStoreRules.MaxSubjectChars, AccountStoreRules.MintSubject(SignIn("discord", atLimit, name)).Length);
        Assert.Throws<ArgumentException>(() => AccountStoreRules.MintSubject(SignIn("discord", atLimit + "7")));
        Assert.Throws<ArgumentException>(() => AccountStoreRules.MintSubject(SignIn("discord", "1", name + "n")));
    }

    [Fact]
    public void MintSubject_Refusals_NameTheRule_AndNeverTheValue()
    {
        const string secretish = "leaky.value-4815162342";
        var refusals = new List<ArgumentException>
        {
            Assert.Throws<ArgumentException>(() => AccountStoreRules.MintSubject(SignIn("discord", secretish))),
            Assert.Throws<ArgumentException>(() => AccountStoreRules.MintSubject(SignIn(secretish, "1"))),
            Assert.Throws<ArgumentException>(
                () => AccountStoreRules.MintSubject(SignIn("discord", "1", secretish + new string('n', 200)))),
        };

        foreach (ArgumentException refusal in refusals)
        {
            Assert.Equal("signIn", refusal.ParamName);
            Assert.DoesNotContain("4815162342", refusal.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateBanReason_AllowsEmptyAndTheLimit_AndRefusesNullAndOneOver()
    {
        AccountStoreRules.ValidateBanReason(string.Empty);
        AccountStoreRules.ValidateBanReason(new string('r', AccountStoreRules.MaxBanReasonChars));

        Assert.Throws<ArgumentNullException>(() => AccountStoreRules.ValidateBanReason(null!));
        ArgumentException overlong = Assert.Throws<ArgumentException>(
            () => AccountStoreRules.ValidateBanReason("4815162342" + new string('r', AccountStoreRules.MaxBanReasonChars)));
        Assert.DoesNotContain("4815162342", overlong.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("discord:1", true)]
    [InlineData("acct:42", true)]
    [InlineData("guest", true)]
    [InlineData("Guest:1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("discord:1.2", false)]
    [InlineData("guest:3", false)]
    [InlineData("discord:4 ", false)]
    [InlineData(" discord:4", false)]
    public void IsAdmissibleSubject_IsWhatATokenCarriesAndTheJoinGateAdmits(string? subject, bool admissible) =>
        Assert.Equal(admissible, AccountStoreRules.IsAdmissibleSubject(subject));

    // SQL Server compares padded strings, so a provider subject with a trailing space would collide with the same
    // subject without it on the primary key of a legacy table. The seam refuses the shape before any store sees it.
    [Theory]
    [InlineData("discord", "80351110224678912 ")]
    [InlineData("discord", " 80351110224678912")]
    [InlineData("discord", "80351110224678912\t")]
    [InlineData("discord ", "80351110224678912")]
    [InlineData(" discord", "80351110224678912")]
    public void MintSubject_RefusesSurroundingWhitespace(string provider, string providerSubject)
    {
        var e = Assert.Throws<ArgumentException>(() => AccountStoreRules.MintSubject(SignIn(provider, providerSubject)));
        Assert.DoesNotContain("80351110224678912", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MintSubject_KeepsInnerWhitespace() =>
        Assert.Equal("oidc:first last", AccountStoreRules.MintSubject(SignIn("oidc", "first last")));
}
