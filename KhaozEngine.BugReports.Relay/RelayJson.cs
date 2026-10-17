using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Serializer options for the relay's own documents: the report record, the accepted index and work items. Wire and
/// bundle documents never use these. They go through the <c>BugReportJson</c> typed helpers.
/// </summary>
internal static class RelayJson
{
    /// <summary>Web defaults with enums written as camelCase strings. Read only.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
