using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class RecordingGitHubIssueClient : IGitHubIssueClient
{
    public List<GitHubIssueDraft> Issues { get; } = new();
    public List<(int IssueNumber, string Body)> Comments { get; } = new();

    public Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Issues.Add(issue);
        return Task.FromResult(Issues.Count);
    }

    public Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Comments.Add((issueNumber, body));
        return Task.CompletedTask;
    }
}
