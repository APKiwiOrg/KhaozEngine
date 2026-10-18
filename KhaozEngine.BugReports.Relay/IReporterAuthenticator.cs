using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Resolves a client's bearer token to the account that sends a report.</summary>
public interface IReporterAuthenticator
{
    /// <summary>
    /// Answers the reporter behind <paramref name="bearerToken"/>, or null when the token is unknown, expired or revoked.
    /// </summary>
    ValueTask<ReporterIdentity?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken);
}
