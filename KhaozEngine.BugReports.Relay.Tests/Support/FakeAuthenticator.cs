using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class FakeAuthenticator : IReporterAuthenticator
{
    public Exception? Failure { get; set; }
    public string? LastToken { get; private set; }

    public ValueTask<ReporterIdentity?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastToken = bearerToken;
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(bearerToken switch
        {
            "good" => new ReporterIdentity("discord:42", "Ash", null),
            "other" => new ReporterIdentity("discord:7", "Bo", "bo#2"),
            _ => (ReporterIdentity?)null,
        });
    }
}
