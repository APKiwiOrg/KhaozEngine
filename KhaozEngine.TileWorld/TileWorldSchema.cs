using System;
using System.IO;

namespace KhaozEngine.TileWorld;

/// <summary>Accessors for the JSON schemas embedded in this package.</summary>
public static class TileWorldSchema
{
    // Lazy rather than a plain static field initializer: a missing resource then surfaces as the
    // TileWorldException itself on every call, not as a TypeInitializationException wrapping it once.
    static readonly Lazy<string> CatalogJson =
        new(() => Read("KhaozEngine.TileWorld.tileworld.catalog.schema.json"));

    /// <summary>The catalog schema (materials and archetypes) as JSON text, read from the embedded
    /// resource once and cached.</summary>
    public static string GetCatalogJson() => CatalogJson.Value;

    static string Read(string name)
    {
        using Stream s = typeof(TileWorldSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new TileWorldException($"embedded schema {name} is missing from the assembly");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
