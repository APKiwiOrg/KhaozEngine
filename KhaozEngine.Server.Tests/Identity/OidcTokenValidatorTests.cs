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
            return MintIdTokenCore(new Dictionary<string, object> { ["sub"] = sub }, aud, expUtc, issuerOverride);
        }

        public string MintIdTokenWithoutSub(string aud, DateTime expUtc, string? issuerOverride = null)
        {
            return MintIdTokenCore(new Dictionary<string, object>(), aud, expUtc, issuerOverride);
        }

        // Signs a valid-looking token with a throwaway key the JWKS never advertises, so the signature cannot
        // verify against the published key. This is the ValidateIssuerSigningKey boundary, not a claim check.
        public string MintWithForeignKey(string sub, string aud, DateTime expUtc)
        {
            RSA foreign = RSA.Create(2048);
            return MintIdTokenCore(new Dictionary<string, object> { ["sub"] = sub }, aud, expUtc, null, foreign);
        }

        // Builds an unsigned alg:none token (empty signature segment), the shape a validator that trusts the
        // header algorithm would wrongly accept.
        public static string MintAlgNone(string sub, string aud, string issuer, DateTime expUtc)
        {
            long exp = new DateTimeOffset(expUtc).ToUnixTimeSeconds();
            string header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
            string payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(
                $"{{\"sub\":\"{sub}\",\"aud\":\"{aud}\",\"iss\":\"{issuer}\",\"exp\":{exp}}}"));
            return $"{header}.{payload}.";
        }

        private string MintIdTokenCore(
            Dictionary<string, object> claims, string aud, DateTime expUtc, string? issuerOverride, RSA? signingKey = null)
        {
            RsaSecurityKey key = new(signingKey ?? Rsa) { KeyId = Kid };
            SigningCredentials creds = new(key, SecurityAlgorithms.RsaSha256);
            JsonWebTokenHandler handler = new();
            SecurityTokenDescriptor descriptor = new()
            {
                Issuer = issuerOverride ?? Authority,
                Audience = aud,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = expUtc,
                SigningCredentials = creds,
                Claims = claims,
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

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("provider timed out"));
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class WrappedCancellationHandler(Action cancel) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            cancel();
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("discovery failed after cancellation"));
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

    [Fact]
    public async Task Token_with_no_sub_claim_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintIdTokenWithoutSub("client-1", DateTime.UtcNow.AddHours(1));
        Assert.Null(await v.ValidateAsync(tok));
    }

    [Fact]
    public async Task Token_signed_with_a_foreign_key_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintWithForeignKey("sub-abc", "client-1", DateTime.UtcNow.AddHours(1));
        Assert.Null(await v.ValidateAsync(tok));
    }

    [Fact]
    public async Task Token_with_a_corrupted_signature_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = f.MintIdToken("sub-abc", "client-1", DateTime.UtcNow.AddHours(1));
        Assert.Null(await v.ValidateAsync(CorruptSignature(tok)));
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        (OidcTokenValidator v, FakeOidc f) = Build();
        string tok = FakeOidc.MintAlgNone("sub-abc", "client-1", f.Authority, DateTime.UtcNow.AddHours(1));
        Assert.Null(await v.ValidateAsync(tok));
    }

    [Fact]
    public async Task Discovery_503_is_provider_unavailable()
    {
        using HttpClient http = new(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        IIdentityValidator validator = new OidcTokenValidator(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1" }, http);

        IdentityValidation result = await validator.ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.ProviderUnavailable, result.Outcome);
        Assert.Contains("OIDC metadata", result.Detail);
    }

    [Fact]
    public async Task Provider_timeout_is_provider_unavailable()
    {
        using HttpClient http = new(new TimeoutHandler());
        IIdentityValidator validator = new OidcTokenValidator(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1" }, http);

        IdentityValidation result = await validator.ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.ProviderUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Foreign_signing_key_is_refused_after_provider_answered()
    {
        (OidcTokenValidator validator, FakeOidc provider) = Build();
        string token = provider.MintWithForeignKey("sub-abc", "client-1", DateTime.UtcNow.AddHours(1));

        IdentityValidation result = await validator.ValidateDetailedAsync(token);

        Assert.Equal(IdentityValidationOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using HttpClient http = new(new CancellationHandler());
        var validator = new OidcTokenValidator(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1" }, http);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateDetailedAsync("token", cts.Token));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_with_warm_configuration_cache()
    {
        (OidcTokenValidator validator, FakeOidc provider) = Build();
        string token = provider.MintIdToken("sub-abc", "client-1", DateTime.UtcNow.AddHours(1));
        Assert.Equal(IdentityValidationOutcome.Verified, (await validator.ValidateDetailedAsync(token)).Outcome);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateDetailedAsync(token, cts.Token));
    }

    [Fact]
    public async Task Wrapped_discovery_failure_propagates_when_caller_was_cancelled()
    {
        using var cts = new CancellationTokenSource();
        using HttpClient http = new(new WrappedCancellationHandler(cts.Cancel));
        var validator = new OidcTokenValidator(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1" }, http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateDetailedAsync("token", cts.Token));
    }

    // Flips the first character of the base64url signature segment so the decoded signature differs from the
    // real one (the first char carries six significant bits of byte 0, so the change is never a no-op), leaving
    // the header/payload intact for the signature check to reject.
    private static string CorruptSignature(string jwt)
    {
        string[] parts = jwt.Split('.');
        char[] signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        parts[2] = new string(signature);
        return string.Join('.', parts);
    }
}
