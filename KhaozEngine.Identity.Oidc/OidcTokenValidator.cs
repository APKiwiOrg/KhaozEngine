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
        ct.ThrowIfCancellationRequested();
        OpenIdConnectConfiguration cfg = await config.GetConfigurationAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await ValidateAgainstConfigurationAsync(credentialToken, cfg).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates while keeping provider transport failure separate from a credential refusal. Discovery and JWKS
    /// retrieval are the transport half. Once configuration arrives, every signature or claim failure is a refusal.
    /// Caller cancellation still propagates.
    /// </summary>
    public async Task<IdentityValidation> ValidateDetailedAsync(
        string credentialToken,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        OpenIdConnectConfiguration cfg;
        try
        {
            cfg = await config.GetConfigurationAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("OIDC validation was cancelled.", ex, ct);
        }
        catch (Exception ex)
        {
            return IdentityValidation.ProviderUnavailable("OIDC metadata request failed: " + ex.Message);
        }

        ct.ThrowIfCancellationRequested();

        VerifiedIdentity? identity = await ValidateAgainstConfigurationAsync(credentialToken, cfg).ConfigureAwait(false);
        return identity is { } verified
            ? IdentityValidation.Verified(verified)
            : IdentityValidation.Refused("OIDC provider answered, but the token was invalid");
    }

    private async Task<VerifiedIdentity?> ValidateAgainstConfigurationAsync(
        string credentialToken,
        OpenIdConnectConfiguration cfg)
    {
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
        if (!jwt.TryGetClaim("sub", out System.Security.Claims.Claim? subClaim) || string.IsNullOrEmpty(subClaim.Value))
        {
            return null;
        }

        string subject = subClaim.Value;
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
