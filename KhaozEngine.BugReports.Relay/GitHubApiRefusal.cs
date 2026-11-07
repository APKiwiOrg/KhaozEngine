namespace KhaozEngine.BugReports.Relay;

/// <summary>Rules that refuse a GitHub API response.</summary>
public enum GitHubApiRefusal
{
    /// <summary>Refuses a response whose status the client treats as a failure, rate limits included.</summary>
    HttpFailure,

    /// <summary>Refuses a response the client did not treat as a failure, because a part it needs cannot be read.
    /// An update's followed redirect is judged by its destination, so <see cref="GitHubApiException.StatusCode"/> can
    /// be a redirect or a failure status here.</summary>
    InvalidSuccessResponse,
}
