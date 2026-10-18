using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>What one relay instance serves and where it files issues.</summary>
public sealed record BugReportRelayOptions
{
    /// <summary>The game id every accepted bundle must carry.</summary>
    public required string GameId { get; init; }

    /// <summary>The game name shown in issue titles.</summary>
    public required string GameDisplayName { get; init; }

    /// <summary>The owner of the repository that receives issues.</summary>
    public required string RepositoryOwner { get; init; }

    /// <summary>The repository that receives issues.</summary>
    public required string RepositoryName { get; init; }

    /// <summary>Labels applied to every filed issue.</summary>
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>The public URL the host serves the Public area under.</summary>
    public required Uri PublicMediaBase { get; init; }

    /// <summary>The shared secret a game server presents with a snapshot.</summary>
    public required string ServiceKey { get; init; }

    /// <summary>Reporter subjects refused with 403.</summary>
    public IReadOnlySet<string> BlockedSubjects { get; init; } = new HashSet<string>();

    /// <summary>Reports one account may send per UTC hour.</summary>
    public int PerAccountPerHour { get; init; } = 5;

    /// <summary>Reports one account may send per UTC day.</summary>
    public int PerAccountPerDay { get; init; } = 20;

    /// <summary>Reports the relay accepts per UTC day across every account.</summary>
    public int GlobalPerDay { get; init; } = 100;

    /// <summary>The largest bundle the relay reads.</summary>
    public long MaxBundleBytes { get; init; } = BugReportLimits.MaxBundleBytes;

    /// <summary>The facts table rows in each issue.</summary>
    public IReadOnlyList<IssueFact> IssueFacts { get; init; } = IssueFact.EngineDefaults;

    /// <summary>The relay's logger. Null uses <c>Log.Get("BugReports.Relay")</c>.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Prints the game and repository only, so a logged record never carries the service key.</summary>
    public override string ToString() =>
        $"{nameof(BugReportRelayOptions)} {{ GameId = {GameId}, Repository = {RepositoryOwner}/{RepositoryName} }}";
}
