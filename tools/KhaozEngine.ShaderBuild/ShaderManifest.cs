using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KhaozEngine.ShaderBuild;

/// <summary>A project's shader programs, read from its programs.json.</summary>
public sealed record ShaderManifest(IReadOnlyList<ManifestProgram> Programs)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>Reads and validates a manifest whose sources resolve against its own folder.</summary>
    public static ShaderManifest Load(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var programs = new List<ManifestProgram>();
        string? current = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in Required(document.RootElement, "programs", JsonValueKind.Array).EnumerateArray())
            {
                current = RequiredString(element, "id");
                if (!seen.Add(current))
                    throw new FormatException("the id appears twice");
                programs.Add(ReadProgram(current, element, folder));
                current = null;
            }
            return new ShaderManifest(programs);
        }
        catch (Exception problem) when (problem is FormatException or JsonException or InvalidOperationException or IOException)
        {
            var subject = current is null ? "" : $" program '{current}'";
            throw new ShaderBuildException(
                ShaderBuildFailure.ManifestInvalid, $"The shader manifest {path}{subject} is invalid: {problem.Message}", innerException: problem)
            { ProgramId = current };
        }
    }

    private static ManifestProgram ReadProgram(string id, JsonElement element, string folder)
    {
        var source = RequiredString(element, "source");
        if (Path.IsPathRooted(source) || source.Split('/', '\\').AsSpan().Contains(".."))
            throw new FormatException("'source' must be a path inside the manifest folder");
        if (!File.Exists(Path.Combine(folder, source)))
            throw new FormatException($"'source' {source} does not exist");
        var vertex = RequiredIdentifier(element, "vertex");
        var fragment = RequiredIdentifier(element, "fragment");
        var precise = element.TryGetProperty("vertexPreciseMath", out var preciseElement) && Boolean(preciseElement, "vertexPreciseMath");
        return new ManifestProgram(id, source, vertex, fragment, precise, ReadVariants(element));
    }

    private static List<ManifestVariant> ReadVariants(JsonElement element)
    {
        var variants = new List<ManifestVariant>();
        if (!element.TryGetProperty("variants", out _))
        {
            variants.Add(new ManifestVariant("", new Dictionary<string, string>(StringComparer.Ordinal)));
            return variants;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in Required(element, "variants", JsonValueKind.Array).EnumerateArray())
        {
            var name = Required(variant, "name", JsonValueKind.String).GetString()!;
            if (name.Length > 0 && !Identifier.IsMatch(name))
                throw new FormatException($"variant name '{name}' must be empty or an identifier");
            if (!names.Add(name))
                throw new FormatException($"variant '{name}' appears twice");
            var defines = new Dictionary<string, string>(StringComparer.Ordinal);
            if (variant.TryGetProperty("defines", out _))
                foreach (var define in Required(variant, "defines", JsonValueKind.Object).EnumerateObject())
                {
                    if (!Identifier.IsMatch(define.Name))
                        throw new FormatException($"define '{define.Name}' must be an identifier");
                    if (define.Value.ValueKind != JsonValueKind.String)
                        throw new FormatException($"define '{define.Name}' must have a string value");
                    defines.Add(define.Name, define.Value.GetString()!);
                }
            variants.Add(new ManifestVariant(name, defines));
        }
        if (variants.Count == 0)
            throw new FormatException("'variants' lists no variant");
        return variants;
    }

    private static bool Boolean(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException($"'{name}' must be true or false"),
    };

    private static string RequiredIdentifier(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        return Identifier.IsMatch(value) ? value : throw new FormatException($"'{name}' must be an identifier");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = Required(element, name, JsonValueKind.String).GetString()!;
        return string.IsNullOrWhiteSpace(value) ? throw new FormatException($"'{name}' must be a non-empty string") : value;
    }

    private static JsonElement Required(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new FormatException($"'{name}' must be a JSON {kind}");
        return value;
    }
}

/// <summary>One program: a Slang source, its vertex and fragment entry points and the variants to compile.</summary>
public sealed record ManifestProgram(
    string Id, string Source, string Vertex, string Fragment, bool VertexPreciseMath, IReadOnlyList<ManifestVariant> Variants);

/// <summary>One compiled variant of a program and the preprocessor defines that select it.</summary>
public sealed record ManifestVariant(string Name, IReadOnlyDictionary<string, string> Defines);
