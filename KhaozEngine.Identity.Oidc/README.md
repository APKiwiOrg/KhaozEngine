# KhaozEngine.Identity.Oidc

Generic OIDC provider and JWKS JWT validator for `KhaozEngine.Identity`: signs a player in via the
authorization-code + PKCE flow, using the system browser and a local loopback redirect.

## Overview

This package supplies the platform-specific pieces `KhaozEngine.Identity` leaves as seams:

- **SystemBrowserLauncher** - `IBrowserLauncher` implementation that opens the sign-in URL in the OS
  default browser via `KhaozEngine.Platform.Browser`.
- **HttpLoopbackListener** - `ILoopbackListener` implementation that binds a short-lived
  `HttpListener` on `http://127.0.0.1:<port>/`, captures the provider's redirect, and returns the
  parsed query string.

It is opt-in and not part of any umbrella package: reference it directly when a game wants generic
OIDC sign-in (Auth0, Okta, Azure AD, or any standards-compliant OIDC provider). Discord and other
provider-specific integrations are separate sibling packages.

## Usage

```csharp
using KhaozEngine.Identity;
using KhaozEngine.Identity.Oidc;

IBrowserLauncher launcher = new SystemBrowserLauncher();
using ILoopbackListener loopback = new HttpLoopbackListener(port: 0); // 0 = ephemeral port

bool opened = await launcher.LaunchAsync(authorizationUri);
LoopbackResult redirect = await loopback.WaitForRedirectAsync(cancellationToken);
// redirect.Query["code"], redirect.Query["state"], etc.
```

The OIDC provider and JWKS-backed JWT validator that drive the authorization-code + PKCE exchange
land alongside these seams as this package matures.

## Dependencies

References `KhaozEngine.Identity` (core seam) and `KhaozEngine.Platform` (browser launch), plus
`Microsoft.IdentityModel.Protocols.OpenIdConnect` and `Microsoft.IdentityModel.JsonWebTokens` for
discovery-document and JWKS-backed token validation. These HTTP/IdentityModel dependencies are
intentionally isolated to this opt-in package; `KhaozEngine.Identity` itself stays transport-agnostic.
