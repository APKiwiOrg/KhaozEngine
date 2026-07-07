# KhaozEngine.Identity

Pluggable player-identity seam: provider sign-in + server-side verified-subject validation + HMAC session tokens, via the exchange model.

## Overview

`KhaozEngine.Identity` provides transport-agnostic core types and interfaces for authenticating players across KhaozEngine games. It defines:

- **IIdentityProvider** - Client-side sign-in integration (e.g., OIDC, Discord)
- **IIdentityValidator** - Server-side credential verification to a stable subject
- **ProviderCredential** - Client sign-in result with refresh state
- **VerifiedIdentity** - Server-verified subject + claims

Provider implementations (OIDC, Discord) and session management are opt-in sibling packages. This core package depends only on `KhaozEngine.Diagnostics` and `KhaozEngine.Serialization`.

## Usage

```csharp
// Client sign-in
IIdentityProvider provider = GetProvider("oidc");
ProviderCredential cred = await provider.SignInAsync();

// Server validation
IIdentityValidator validator = GetValidator("oidc");
VerifiedIdentity? verified = await validator.ValidateAsync(cred.CredentialToken);
```

## Future

- SessionToken HMAC-secured session identifiers
- Orchestrator for multi-provider coordination
- `KhaozEngine.Identity.Oidc` - OpenID Connect provider backend
- `KhaozEngine.Identity.Discord` - Discord OAuth2 provider backend
