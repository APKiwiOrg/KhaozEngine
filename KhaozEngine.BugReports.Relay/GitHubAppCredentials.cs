namespace KhaozEngine.BugReports.Relay;

/// <summary>The GitHub App identity the relay files issues as.</summary>
/// <param name="AppId">The GitHub App id.</param>
/// <param name="InstallationId">The installation id on the target repository's owner.</param>
/// <param name="PrivateKeyPem">The app's RSA private key in PKCS#1 or PKCS#8 PEM.</param>
public sealed record GitHubAppCredentials(long AppId, long InstallationId, string PrivateKeyPem)
{
    /// <summary>Prints the ids only, so a logged record never carries the private key.</summary>
    public override string ToString() =>
        $"{nameof(GitHubAppCredentials)} {{ AppId = {AppId}, InstallationId = {InstallationId} }}";
}
