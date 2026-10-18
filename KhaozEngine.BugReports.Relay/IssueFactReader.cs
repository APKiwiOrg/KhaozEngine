using System.Text.Json;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

internal static class IssueFactReader
{
    public static string? Read(IssueInput input, IssueFact fact)
    {
        foreach (CapturedSection section in input.Sections)
        {
            if (section.Key != fact.Section) continue;
            using JsonDocument document = JsonDocument.Parse(section.Json, BugReportJson.SectionDocumentOptions);
            return ReadValue(document.RootElement, fact.Property);
        }
        return input.Snapshot is not null && input.Snapshot.Sections.TryGetValue(fact.Section, out JsonElement value)
            ? ReadValue(value, fact.Property) : null;
    }

    static string? ReadValue(JsonElement value, string path)
    {
        foreach (string property in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out value)) return null;
        }
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(value),
            _ => null,
        };
        return text is null ? null : IssueFactText.Limit(text);
    }
}
