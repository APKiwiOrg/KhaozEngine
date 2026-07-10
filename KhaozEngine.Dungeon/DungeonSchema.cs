using System.IO;

namespace KhaozEngine.Dungeon;

/// <summary>Access to the packaged dungeon config/layout JSON schema. The root schema is a
/// <c>oneOf</c> over two named <c>$defs</c> entries, "config" (matching <see cref="DungeonJson.SaveConfig"/>
/// output) and "layout" (matching <see cref="DungeonJson.SaveLayout"/> output), so one embedded resource
/// covers both document kinds for build-time validation (KhaozEngine.Content) and editor/AI tooling.</summary>
public static class DungeonSchema
{
    const string ResourceName = "KhaozEngine.Dungeon.dungeon.schema.json";

    /// <summary>The schema JSON text.</summary>
    public static string GetJson()
    {
        using Stream stream = typeof(DungeonSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
