using System;
using System.Text.Json;

namespace KhaozEngine.BugReports.Client;

/// <summary>The <c>build</c> section: <c>{gameId, gameName, version, buildName, engineVersion, configuration, commit}</c>.</summary>
/// <param name="facts">Returns the build facts. Called in the trigger frame, so it must do no I/O.</param>
internal sealed class BuildSection(Func<BuildFacts> facts) : IBugReportSection
{
    /// <inheritdoc />
    public string Key => "build";

    /// <inheritdoc />
    public int MaxBytes => 4 * 1024;

    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        BuildFacts build = facts();
        writer.WriteStartObject();
        writer.WriteString("gameId", build.GameId);
        writer.WriteString("gameName", build.GameName);
        writer.WriteString("version", build.Version);
        writer.WriteString("buildName", build.BuildName);
        writer.WriteString("engineVersion", build.EngineVersion);
        writer.WriteString("configuration", build.Configuration);
        writer.WriteString("commit", build.Commit);
        writer.WriteEndObject();
    }
}
