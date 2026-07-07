using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Oidc;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class OidcTokenValidatorTests
{
    // Builds a fake OIDC endpoint set (discovery + jwks) and mints signed id_tokens with a local RSA key.
    private sealed class FakeOidc
    {
        public readonly RSA Rsa = RSA.Create(2048);
        public readonly string Authority = "https://issuer.test";
        public readonly string Kid = "test-key-1";

        public string Discovery() => $"{{\"issuer\":\"{Authority}\",\"jwks_uri\":\"{Authority}/jwks\"}}";

        public string Jwks()
        {
            RSAParameters p = Rsa.ExportParameters(false);
            string n = Base64UrlEncoder.Encode(p.Modulus);
            string e = Base64UrlEncoder.Encode(p.Exponent);
            return $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"sig\",\"kid\":\"{Kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}]}}";
        }

        public string MintIdToken(string sub, string aud, DateTime expUtc, string? issuerOverride = null)
        {
            RsaSecurityKey key = new(Rsa) { KeyId = Kid };
            SigningCredentials creds = new(key, SecurityAlgorithms.RsaSha256);
            JsonWebTokenHandler handler = new();
            SecurityTokenDescriptor descriptor = new()
            {
                Issuer = issuerOverride ?? Authority,
                Audience = aud,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = expUtc,
                SigningCredentials = creds,
                Claims = new Dictionary<string, object> { ["sub"] = sub },
            };
            return handler.CreateToken(descriptor);
        }
    }

    private sealed class FakeHandler(FakeOidc f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string body = req.RequestUri!.AbsolutePath.Contains("jwks") ? f.Jwks() : f.Discovery();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (OidcTokenValidator v, FakeOidc f) Build()
    {
        FakeOidc f = new();
        HttpClient http = new(new FakeHandler(f));
        OidcTokenValidator v = new(new OidcProviderOptions { Authority = f.Authority, ClientId = "client-1" }, http);
        return (v, f);
    }

    [Fact]
    public async Task Valid_token_yields_subject()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintIdToken("sub-abc", "client-1", DateTime.UtcNow.AddHours(1));
        VerifiedIdentity? id = await v.ValidateAsync(tok);
        Assert.NotNull(id);
        Assert.Equal("sub-abc", id!.Value.Subject);
        Assert.Equal("oidc", id.Value.ProviderId);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintIdToken("sub-abc", "other-client", DateTime.UtcNow.AddHours(1));
        Assert.Null(await v.ValidateAsync(tok));
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintIdToken("sub-abc", "client-1", DateTime.UtcNow.AddHours(-1));
        Assert.Null(await v.ValidateAsync(tok));
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintIdToken("sub-abc", "client-1", DateTime.UtcNow.AddHours(1), issuerOverride: "https://evil.test");
        Assert.Null(await v.ValidateAsync(tok));
    }
}
