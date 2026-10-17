using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.BugReports.Contracts;

[JsonConverter(typeof(BugReportKindJsonConverter))]
public enum BugReportKind
{
    Bug,
    Crash,
}

public sealed class BugReportKindJsonConverter : JsonStringEnumConverter<BugReportKind>
{
    public BugReportKindJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}
