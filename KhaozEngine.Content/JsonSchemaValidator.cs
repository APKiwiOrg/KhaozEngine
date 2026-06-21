using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Json.Schema;
using KhaozEngine.Serialization;

namespace KhaozEngine.Content;

/// <summary>Result of validating a JSON instance against a schema.</summary>
public sealed record ValidationReport(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>Validates JSON instances against JSON Schema (via Json.Schema / JsonSchema.Net).
/// Instances and schemas are parsed with the engine JSONC policy (<see cref="Jsonc"/>), so data files may carry
/// comments and trailing commas.</summary>
public static class JsonSchemaValidator
{
    /// <summary>Validates an instance JSON string against a schema JSON string.</summary>
    public static ValidationReport Validate(string instanceJson, string schemaJson)
    {
        // Use an isolated SchemaRegistry so repeated calls with schemas sharing the same $id
        // (e.g. two data files pointing at the same schema) do not crash on "overwriting
        // registered schemas is not permitted" in the global static SchemaRegistry.
        BuildOptions buildOpts = new() { SchemaRegistry = new SchemaRegistry() };
        JsonSchema schema = JsonSchema.FromText(schemaJson, buildOpts);
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
        return new ValidationReport(false, errors);
    }

    /// <summary>Validates every <c>*.json</c> in <paramref name="dataDir"/> against the schema named by its
    /// <c>$schema</c> property (a path relative to <paramref name="dataDir"/>, e.g. "schemas/x.schema.json").
    /// Logs results to <paramref name="log"/>. Returns true iff all schema'd files pass; files without a
    /// <c>$schema</c> are warned and skipped.</summary>
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
