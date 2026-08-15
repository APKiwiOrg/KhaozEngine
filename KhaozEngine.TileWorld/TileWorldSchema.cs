using System.IO;
using System.Reflection;

namespace KhaozEngine.TileWorld;

/// <summary>Accessors for the JSON schemas embedded in this package.</summary>
public static class TileWorldSchema
{
    /// <summary>The catalog schema (materials and archetypes) as JSON text.</summary>
    public static string GetCatalogJson() => Read("KhaozEngine.TileWorld.tileworld.catalog.schema.json");

    static string Read(string name)
    {
        using Stream s = typeof(TileWorldSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new TileWorldException($"embedded schema {name} is missing from the assembly");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
