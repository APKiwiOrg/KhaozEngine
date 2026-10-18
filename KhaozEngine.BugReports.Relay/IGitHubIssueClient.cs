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

/// <summary>The two GitHub calls the worker makes. Every failure throws <see cref="GitHubApiException"/>.</summary>
public interface IGitHubIssueClient
{
    /// <summary>Files <paramref name="issue"/> and answers its issue number.</summary>
    Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken);

    /// <summary>Adds a comment with <paramref name="body"/> to issue <paramref name="issueNumber"/>.</summary>
    Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken);
}

/// <summary>A failed GitHub API call. The host redelivers the work item that raised it.</summary>
public sealed class GitHubApiException : Exception
{
    /// <summary>Creates the exception for a response with <paramref name="statusCode"/>.</summary>
    public GitHubApiException(int statusCode, bool isRateLimited, TimeSpan? retryAfter, string message)
        : base(message)
    {
        StatusCode = statusCode;
        IsRateLimited = isRateLimited;
        RetryAfter = retryAfter;
    }

    /// <summary>The HTTP status code GitHub answered with.</summary>
    public int StatusCode { get; }

    /// <summary>True when GitHub refused the call for a primary or secondary rate limit.</summary>
    public bool IsRateLimited { get; }

    /// <summary>How long GitHub asked the caller to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}
