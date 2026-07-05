# Security

KhaozEngine's security posture, threat model, and layered defenses are documented in
[docs/SECURITY-BASELINE.md](docs/SECURITY-BASELINE.md). Read that first.

## Reporting a vulnerability

Report privately. Do not open a public issue for a suspected vulnerability.

- Preferred: GitHub private vulnerability reporting (this repo's **Security -> Advisories ->
  Report a vulnerability**).
- Include enough to reproduce: affected package/version, the input or feed that triggers it, and impact.

The highest-impact surface is the update channel (`KhaozEngine.Updates`): a spoofed feed is RCE across
games. Its hardening is covered in the baseline doc and [UPDATER.md](docs/UPDATER.md).
