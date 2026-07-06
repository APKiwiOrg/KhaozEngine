using System;
using KhaozEngine.Commerce;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

public class ValueTypesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AccountId_rejects_empty(string? value)
    {
        Assert.Throws<ArgumentException>(() => new AccountId(value!));
    }

    [Fact]
    public void AccountId_equality_is_ordinal_by_value()
    {
        Assert.Equal(new AccountId("acct:1"), new AccountId("acct:1"));
        Assert.NotEqual(new AccountId("acct:1"), new AccountId("acct:2"));
    }

    [Fact]
    public void CurrencyId_wraps_value()
    {
        Assert.Equal("shard", new CurrencyId("shard").Value);
    }
}
