using System.IO;
using System.Reflection;

namespace KhaozEngine.MapDoc;

/// <summary>Access to the packaged map document JSON schema. Consumers materialize it into their data
/// directory (<see cref="WriteTo"/>) so map files' $schema references resolve for build-time validation
/// (KhaozEngine.Content) and editor/AI tooling.</summary>
public static class MapDocumentSchema
{
    const string ResourceName = "KhaozEngine.MapDoc.mapdoc.schema.json";

    /// <summary>The schema JSON text.</summary>
    public static string GetJson()
    {
        using Stream stream = typeof(MapDocumentSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Writes the schema to <paramref name="path"/>, creating the directory if needed.</summary>
    public static void WriteTo(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, GetJson());
    }
}
