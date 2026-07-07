using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace KhaozEngine.Identity.Oidc;

/// <summary>Validates an OIDC id_token against the issuer's discovery doc + JWKS and returns the verified subject.</summary>
public sealed class OidcTokenValidator : IIdentityValidator
{
    private readonly OidcProviderOptions options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> config;

    public string ProviderId => "oidc";

    public OidcTokenValidator(OidcProviderOptions options, HttpClient? httpClient = null)
    {
        this.options = options;
        string metadata = options.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
        HttpDocumentRetriever docRetriever = httpClient is null ? new HttpDocumentRetriever() : new HttpDocumentRetriever(httpClient);
        config = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever(), docRetriever);
    }

    public async Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default)
    {
        OpenIdConnectConfiguration cfg = await config.GetConfigurationAsync(ct).ConfigureAwait(false);
        TokenValidationParameters parameters = new()
        {
            ValidIssuer = cfg.Issuer,
            ValidAudience = options.ClientId,
            IssuerSigningKeys = cfg.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
        JsonWebTokenHandler handler = new();
        TokenValidationResult result = await handler.ValidateTokenAsync(credentialToken, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            return null;
        }

        JsonWebToken jwt = (JsonWebToken)result.SecurityToken;
        string subject = jwt.GetClaim("sub").Value;
        Dictionary<string, string> claims = new(StringComparer.Ordinal);
        foreach (System.Security.Claims.Claim claim in jwt.Claims)
        {
            claims[claim.Type] = claim.Value;
        }

        string? displayName = claims.TryGetValue("name", out string? name)
            ? name
            : claims.TryGetValue("preferred_username", out string? preferredUsername) ? preferredUsername : null;

        return new VerifiedIdentity(subject, ProviderId, displayName, claims);
    }
}
