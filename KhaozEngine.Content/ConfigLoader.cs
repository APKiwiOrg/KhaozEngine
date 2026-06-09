using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace KhaozEngine.Content;

/// <summary>Loads typed config from JSON — the disk path first (if it exists), else an embedded resource.</summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads <typeparamref name="T"/> from <paramref name="diskPath"/> if it exists, otherwise from
    /// the embedded resource <paramref name="resourceName"/> in <paramref name="assembly"/>. Throws if neither
    /// is found or deserialization yields null.</summary>
    public static T Load<T>(Assembly assembly, string resourceName, string? diskPath = null, JsonSerializerOptions? options = null)
    {
        string json;
        if (diskPath is not null && File.Exists(diskPath))
        {
            json = File.ReadAllText(diskPath);
        }
        else
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                throw new InvalidOperationException(
                    $"Config not found: no file at '{diskPath ?? "(none)"}' and no embedded resource '{resourceName}' in {assembly.GetName().Name}.");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        return JsonSerializer.Deserialize<T>(json, options ?? Default)
            ?? throw new InvalidOperationException($"Config '{resourceName}' deserialized to null.");
    }
}
