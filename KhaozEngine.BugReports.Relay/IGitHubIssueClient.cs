using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>An issue ready to file.</summary>
/// <param name="Title">The issue title.</param>
/// <param name="Body">The issue body in GitHub Markdown.</param>
/// <param name="Labels">The labels to apply.</param>
public sealed record GitHubIssueDraft(string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>Replaces an issue's title and body and closes it as not planned.</summary>
/// <param name="Title">The replacement issue title.</param>
/// <param name="Body">The replacement issue body in GitHub Markdown.</param>
public sealed record GitHubIssueUpdate(string Title, string Body);

/// <summary>The result of an issue update attempt.</summary>
public enum GitHubIssueUpdateOutcome
{
    /// <summary>The issue was updated.</summary>
    Updated,
    /// <summary>The issue no longer exists.</summary>
    Gone,
    /// <summary>The issue moved to another issue page.</summary>
    Moved,
}

/// <summary>An issue update's outcome and issue page URL.</summary>
/// <param name="Outcome">Whether the issue was updated, is gone or moved.</param>
/// <param name="IssueUrl">The GitHub issue page URL for Updated or the new page for Moved, null for Gone.</param>
public sealed record GitHubIssueUpdateResult(GitHubIssueUpdateOutcome Outcome, Uri? IssueUrl);

/// <summary>The GitHub calls the worker makes. API failures throw <see cref="GitHubApiException"/>.</summary>
public interface IGitHubIssueClient
{
    /// <summary>Files <paramref name="issue"/> and answers its issue number.</summary>
    Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken);

    /// <summary>Adds a comment with <paramref name="body"/> to issue <paramref name="issueNumber"/>.</summary>
    Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>Returns the issue page URL, or null when this client cannot construct the page URL.</summary>
    Uri? IssuePageUrl(int issueNumber) => null;

    /// <summary>Replaces the title and body and closes the issue as not planned, preserving labels.</summary>
    Task<GitHubIssueUpdateResult> UpdateIssueAsync(int issueNumber, GitHubIssueUpdate update, CancellationToken cancellationToken)
        => throw new NotSupportedException("IGitHubIssueClient.UpdateIssueAsync is not implemented by this client.");
}

/// <summary>A failed GitHub API call. The host redelivers the work item that raised it.</summary>
public sealed class GitHubApiException : Exception
{
    /// <summary>Creates the exception for a response with <paramref name="statusCode"/>, with no reason.</summary>
    public GitHubApiException(int statusCode, bool isRateLimited, TimeSpan? retryAfter, string message)
        : base(message)
    {
        StatusCode = statusCode;
        IsRateLimited = isRateLimited;
        RetryAfter = retryAfter;
    }

    /// <summary>Creates the exception for a response with <paramref name="statusCode"/> refused by
    /// <paramref name="reason"/>.</summary>
    /// <param name="reason">The rule that refused the response.</param>
    /// <param name="statusCode">The HTTP status code GitHub answered with.</param>
    /// <param name="isRateLimited">True when GitHub refused the call for a rate limit.</param>
    /// <param name="retryAfter">How long GitHub asked the caller to wait, when it said.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public GitHubApiException(GitHubApiRefusal reason, int statusCode, bool isRateLimited, TimeSpan? retryAfter, string message)
        : base(message)
    {
        Reason = reason;
        StatusCode = statusCode;
        IsRateLimited = isRateLimited;
        RetryAfter = retryAfter;
    }

    /// <summary>The rule that refused the response. Null only for an instance built through the message-only
    /// constructor by code outside the engine, such as another <see cref="IGitHubIssueClient"/>.</summary>
    public GitHubApiRefusal? Reason { get; }

    /// <summary>The HTTP status code GitHub answered with.</summary>
    public int StatusCode { get; }

    /// <summary>True when GitHub refused the call for a primary or secondary rate limit.</summary>
    public bool IsRateLimited { get; }

    /// <summary>How long GitHub asked the caller to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>GitHub's own <c>message</c> for <see cref="GitHubApiRefusal.HttpFailure"/>, at most 200 characters. It
    /// is untrusted text, and never the raw body. Null when the body holds no such message, or when the whole message
    /// was dropped because it held a credential.</summary>
    public string? UpstreamDetail { get; init; }

    /// <summary>The part that could not be read, for <see cref="GitHubApiRefusal.InvalidSuccessResponse"/>.</summary>
    public GitHubResponsePart? ResponsePart { get; init; }
}
