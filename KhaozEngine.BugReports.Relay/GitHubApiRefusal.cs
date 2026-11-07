namespace KhaozEngine.BugReports.Relay;

/// <summary>Rules that refuse a GitHub API response.</summary>
public enum GitHubApiRefusal
{
    /// <summary>Refuses a response whose status the client treats as a failure, rate limits included.</summary>
    HttpFailure,

    /// <summary>Refuses a success response because a part the client needs cannot be read. GitHub answered success, so a
    /// create may already have taken effect.</summary>
    InvalidSuccessResponse,

    /// <summary>Refuses an update GitHub answered with a move, because the destination is not an issue on the API host.
    /// <see cref="GitHubApiException.StatusCode"/> is the 301 or the followed status, which can be a failure. Nothing
    /// shows the update was applied.</summary>
    RedirectDestinationUnusable,
}
