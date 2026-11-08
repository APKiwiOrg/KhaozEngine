using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Json.Schema;
using KhaozEngine.Serialization;

namespace KhaozEngine.Content;

/// <summary>Validates JSON instances against JSON Schema (via Json.Schema / JsonSchema.Net).
/// Instances and schemas are parsed with the engine JSONC policy (<see cref="Jsonc"/>), so data files may carry
/// comments and trailing commas.</summary>
public static class JsonSchemaValidator
{
    /// <summary>Validates an instance JSON string against a schema JSON string.</summary>
    /// <remarks>A schema that does not parse comes back as an invalid report whose single error names the schema
    /// problem, rather than throwing: one broken schema file must not abort a whole <see cref="ValidateDirectory"/>
    /// sweep, which reports every other data file it was asked about.</remarks>
    public static ValidationReport Validate(string instanceJson, string schemaJson)
    {
        // Use an isolated SchemaRegistry so repeated calls with schemas sharing the same $id
        // (e.g. two data files pointing at the same schema) do not crash on "overwriting
        // registered schemas is not permitted" in the global static SchemaRegistry.
        BuildOptions buildOpts = new() { SchemaRegistry = new SchemaRegistry() };
        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaJson, buildOpts);
        }
        catch (Exception ex)
        {
            // The schema is file content like any other input, so a failure to parse or build it is a data error to
            // report, not a bug to propagate. Deliberately broad: the schema library raises JsonException for broken
            // text but other types for a structurally invalid keyword, and narrowing here would leave the sweep
            // abortable by exactly the class of bad file this guard exists for.
            return new ValidationReport(false, new[] { $"invalid schema: {ex.Message}" });
        }
        using JsonDocument doc = Jsonc.ParseDocument(instanceJson);
        EvaluationResults result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return new ValidationReport(true, Array.Empty<string>());

        var errors = new List<string>();
        if (result.Details is not null)
        {
            foreach (EvaluationResults detail in result.Details)
            {
                if (detail.IsValid || detail.Errors is null) continue;
                foreach (KeyValuePair<string, string> error in detail.Errors)
                    errors.Add($"{detail.InstanceLocation}: {error.Value}");
            }
        }
        if (errors.Count == 0) errors.Add("schema validation failed");
        var findings = new List<JsonSchemaFinding>();
        CollectFindings(result, doc.RootElement, findings);
        return new ValidationReport(false, errors) { Findings = findings };
    }

    static void CollectFindings(EvaluationResults result, JsonElement instance, List<JsonSchemaFinding> findings)
    {
        if (result.IsValid) return;
        if (result.Errors is not null)
        {
            string? propertyName = null;
            if (result.InstanceLocation.SegmentCount > 0 && result.InstanceLocation.GetParent() is { } parent &&
                parent.Evaluate(instance) is { ValueKind: JsonValueKind.Object })
                propertyName = result.InstanceLocation.GetSegment(result.InstanceLocation.SegmentCount - 1).ToString()
                    .Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            foreach (string errorKeyword in result.Errors.Keys)
            {
                string? keyword = errorKeyword;
                // A false subschema has no keyword of its own. The owning additionalProperties applicator
                // is known from the evaluation path, never from the error's prose.
                if (keyword is "false" or "")
                    keyword = result.EvaluationPath.SegmentCount > 0 &&
                        result.EvaluationPath.GetSegment(result.EvaluationPath.SegmentCount - 1).Equals("additionalProperties")
                        ? "additionalProperties" : null;
                findings.Add(new JsonSchemaFinding(result.InstanceLocation.ToString(), keyword, propertyName));
            }
        }
        if (result.Details is not null)
            foreach (EvaluationResults detail in result.Details) CollectFindings(detail, instance, findings);
    }

    /// <summary>Validates every <c>*.json</c> in <paramref name="dataDir"/> against the schema named by its
    /// <c>$schema</c> property (a path relative to <paramref name="dataDir"/>, e.g. "schemas/x.schema.json").
    /// Logs results to <paramref name="log"/>. Returns true iff all schema'd files pass; files without a
    /// <c>$schema</c> are warned and skipped. Every failure mode is per file and the sweep runs to the end: a
    /// missing schema, an instance that does not parse, a schema that does not parse, and a failed validation all
    /// log a FAIL line and move on.</summary>
    public static bool ValidateDirectory(string dataDir, TextWriter log)
    {
        if (!Directory.Exists(dataDir))
        {
            log.WriteLine($"FAIL  data directory not found: {dataDir}");
            return false;
        }

        bool allValid = true;
        foreach (string jsonFile in Directory.EnumerateFiles(dataDir, "*.json"))
        {
            string fileName = Path.GetFileName(jsonFile);
            string json = File.ReadAllText(jsonFile);

            string? schemaRef;
            try { schemaRef = Jsonc.ParseNode(json)?["$schema"]?.GetValue<string>(); }
            catch (JsonException ex) { log.WriteLine($"FAIL  {fileName}: invalid JSON -- {ex.Message}"); allValid = false; continue; }

            if (string.IsNullOrWhiteSpace(schemaRef)) { log.WriteLine($"WARN  {fileName}: no $schema, skipping"); continue; }

            string schemaPath = Path.Combine(dataDir, schemaRef);
            if (!File.Exists(schemaPath)) { log.WriteLine($"FAIL  {fileName}: schema not found at {schemaRef}"); allValid = false; continue; }

            ValidationReport report = Validate(json, File.ReadAllText(schemaPath));
            if (report.IsValid)
            {
                log.WriteLine($"OK    {fileName}");
            }
            else
            {
                allValid = false;
                log.WriteLine($"FAIL  {fileName}:");
                foreach (string e in report.Errors) log.WriteLine($"        {e}");
            }
        }
        return allValid;
    }
}
