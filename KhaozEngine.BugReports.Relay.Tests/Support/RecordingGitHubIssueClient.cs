using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class RecordingGitHubIssueClient : IGitHubIssueClient
{
    readonly object gate = new();
    public List<GitHubIssueDraft> Issues { get; } = new();
    public List<(int IssueNumber, string Body)> Comments { get; } = new();
    public Func<Task>? OnCreate { get; set; }
    public Func<Task>? OnComment { get; set; }
    public int FirstIssueNumber { get; set; } = 7;

    public async Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OnCreate is not null) await OnCreate();
        lock (gate)
        {
            Issues.Add(issue);
            return FirstIssueNumber + Issues.Count - 1;
        }
    }

    public async Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OnComment is not null) await OnComment();
        lock (gate)
            Comments.Add((issueNumber, body));
    }
}
