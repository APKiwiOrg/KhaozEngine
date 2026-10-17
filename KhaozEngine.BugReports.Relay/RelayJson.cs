using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Serializer options for the relay's own documents: the report record, the accepted index and work items. Wire and
/// bundle documents never use these. They go through the <c>BugReportJson</c> typed helpers.
/// </summary>
internal static class RelayJson
{
    /// <summary>
    /// Web defaults with strict numbers, no duplicate properties and enums as their exact camelCase names. Read only.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            AllowDuplicateProperties = false,
            Converters = { new ExactNameEnumConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
