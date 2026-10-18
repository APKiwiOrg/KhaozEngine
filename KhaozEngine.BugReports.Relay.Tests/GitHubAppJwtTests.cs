using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class GitHubAppJwtTests
{
    static readonly DateTimeOffset Clock = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Jwt_header_claims_and_signature_verify()
    {
        using var rsa = RSA.Create(2048);
        string[] segments = GitHubAppJwt.Create(123456, rsa, Clock).Split('.');

        Assert.Equal(3, segments.Length);
        Assert.Equal("{\"alg\":\"RS256\",\"typ\":\"JWT\"}", Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segments[0])));
        string claims = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segments[1]));
        Assert.Equal("{\"iat\":1792195140,\"exp\":1792195740,\"iss\":\"123456\"}", claims);
        using var document = JsonDocument.Parse(claims);
        Assert.Equal(1792195140, document.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(1792195740, document.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal("123456", document.RootElement.GetProperty("iss").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]),
            Base64Url.DecodeFromChars(segments[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Create_NoPaddingCharacters()
    {
        using var rsa = RSA.Create(2048);
        Assert.DoesNotContain('=', GitHubAppJwt.Create(123456, rsa, Clock));
    }
}
