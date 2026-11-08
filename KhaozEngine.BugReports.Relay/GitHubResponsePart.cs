namespace KhaozEngine.BugReports.Relay;

/// <summary>The part of a GitHub response the client could not read.</summary>
public enum GitHubResponsePart
{
    /// <summary>The token or expiry time in an installation token exchange.</summary>
    InstallationToken,

    /// <summary>The number of a created issue.</summary>
    IssueNumber,

    /// <summary>The issue page URL of an updated issue.</summary>
    IssueUrl,
}
