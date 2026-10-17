using System;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

public sealed class ReportIdsTests
{
    [Fact]
    public void NewId_is_32_lowercase_hex_version_7()
    {
        string id = ReportIds.NewId();
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal('7', id[12]);
        Assert.True(ReportIds.IsValid(id));
    }

    [Theory]
    [InlineData("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f", "R-P3TKJZ")]
    [InlineData("0192f1c2a3b47c4d8e9f0a1b00000021", "R-000011")]
    [InlineData("0192f1c2a3b47c4d8e9f0a1bffffffff", "R-ZZZZZZ")]
    public void ReferenceCode_uses_the_last_30_bits_in_crockford_base32(string id, string expected)
        => Assert.Equal(expected, ReportIds.ReferenceCode(id));

    [Theory]
    [InlineData("")]
    [InlineData("0192F1C2A3B47C4D8E9F0A1B2C3D4E5F")]
    [InlineData("0192f1c2a3b44c4d8e9f0a1b2c3d4e5f")]
    [InlineData("0192f1c2-a3b4-7c4d-8e9f-0a1b2c3d4e5f")]
    [InlineData("../../etc/passwd000000000000000000")]
    [InlineData("0192f1c2a3b47c4d7e9f0a1b2c3d4e5f")]
    [InlineData("0192f1c2a3b47c4dce9f0a1b2c3d4e5f")]
    [InlineData("0192f1c2a3b47c4d8e9f0a1b2c3d4e5g")]
    [InlineData("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f\n")]
    public void IsValid_rejects_non_canonical_ids(string id)
        => Assert.False(ReportIds.IsValid(id));

    [Theory]
    [InlineData("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f")]
    [InlineData("0192f1c2a3b47c4d9e9f0a1b2c3d4e5f")]
    [InlineData("0192f1c2a3b47c4dae9f0a1b2c3d4e5f")]
    [InlineData("0192f1c2a3b47c4dbe9f0a1b2c3d4e5f")]
    public void IsValid_accepts_each_version_7_variant(string id)
        => Assert.True(ReportIds.IsValid(id));

    [Fact]
    public void ReferenceCode_and_ServerSnapshot_route_throw_on_invalid_id()
    {
        Assert.Throws<ArgumentException>(() => ReportIds.ReferenceCode("x"));
        Assert.Throws<ArgumentException>(() => RelayRoutes.ServerSnapshot("x"));
    }

    [Fact]
    public void ServerSnapshot_route_is_relative()
        => Assert.Equal("bug-reports/0192f1c2a3b47c4d8e9f0a1b2c3d4e5f/server",
            RelayRoutes.ServerSnapshot("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f"));
}
