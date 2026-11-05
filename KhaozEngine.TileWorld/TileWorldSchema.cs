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
    /// <exception cref="TileWorldException"><see cref="TileWorldRefusal.SchemaResourceMissing"/> carries
    /// the absent resource's <see cref="TileWorldException.ResourceName"/>.</exception>
    public static string GetCatalogJson() => CatalogJson.Value;

    /// <summary>Exercises missing-resource admission with a supplied resource name, without changing the
    /// production schema name or its lazy cache.</summary>
    internal static string ReadMissingResourceForTests(string resourceName) => Read(resourceName);

    static string Read(string name)
    {
        using Stream s = typeof(TileWorldSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new TileWorldException(TileWorldRefusal.SchemaResourceMissing, $"embedded schema {name} is missing from the assembly")
            { ResourceName = name };
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
