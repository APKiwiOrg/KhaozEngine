using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileEdit;

/// <summary>Archetype collision heights: measured from the kit's model meshes, and written into the catalog file
/// that defines each archetype.
///
/// <para>A height is the model's top in metres above its local base, the <c>max.Y</c> of its vertex bounds. That
/// is what a physics box stood on the object's anchor needs to match the drawn model. Measuring uses no greybox
/// fallback, so an archetype whose glb is missing or unreadable is an error entry rather than a guessed box, and a
/// top at or below the base is an error entry too, since the catalog loader refuses a height that is not above 0.
/// Such an archetype takes a hand-set height.</para>
///
/// <para>Writing never touches the world, so it is not an undo step. Each height goes into the ONE catalog file
/// the loader recorded the archetype from (a duplicate id across files is refused at load, so there is exactly
/// one), through <see cref="CatalogWriter"/>, which keeps every byte it does not change. Every file is prepared
/// and checked against the loader before any is written, and the session then reloads its catalogs so the next
/// verb sees the new heights.</para></summary>
public sealed class ArchetypeHeightService(TileEditSession session)
{
    /// <summary>Measures every archetype of the open catalogs against the meshes under <paramref name="kitRoot"/>,
    /// which resolves against the world directory when relative. Read-only.</summary>
    /// <exception cref="TileWorldException">No world is open.</exception>
    /// <exception cref="DirectoryNotFoundException">The kit directory does not exist.</exception>
    public MeasureHeightsResult Measure(string kitRoot)
    {
        string root = session.ResolvePath(kitRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"the kit directory {root} does not exist.");

        // Copied under the lock, measured outside it: loading a kit's glbs is the slow part, and it reads nothing
        // the session owns.
        TileObjectArchetype[] archetypes = session.Read(e => e.Catalogs.Archetypes.Values
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new TileObjectArchetype
            {
                Id = a.Id,
                Name = a.Name,
                MeshRef = a.MeshRef,
                CollisionKind = a.CollisionKind,
                CollisionHeight = a.CollisionHeight,
            })
            .ToArray());

        string? lastLog = null;
        var resolver = new GltfMeshResolver(root, fallback: null, log: line => lastLog = line);
        var bounds = new TileObjectBoundsCache(resolver);
        var entries = new List<MeasuredArchetypeHeight>(archetypes.Length);
        foreach (TileObjectArchetype a in archetypes)
        {
            lastLog = null;
            float? height = null;
            string? error;
            if (bounds.TryGetBounds(a, out _, out Vector3 max))
            {
                error = float.IsFinite(max.Y) && max.Y > 0f
                    ? null
                    : $"the model top is {max.Y.ToString(CultureInfo.InvariantCulture)} m, not above 0, so this " +
                      "archetype needs a hand-set height.";
                if (error is null) height = max.Y;
            }
            else
            {
                error = MissingMesh(resolver, a, lastLog);
            }
            entries.Add(new MeasuredArchetypeHeight(a.Id, a.CollisionKind.ToString(), height, a.CollisionHeight, error));
        }
        return new MeasureHeightsResult(root, entries);
    }

    /// <summary>Writes each height into the catalog file that defines its archetype. An archetype that already
    /// records a height is skipped unless <paramref name="overwrite"/>, and one that already records exactly the
    /// height asked for is always skipped, so its file is not rewritten. A height that is not a finite number above
    /// 0, an id the open catalogs do not define and an id listed twice are error entries, and nothing is written
    /// for them.</summary>
    /// <exception cref="TileWorldException">No world is open, or a catalog file no longer loads.</exception>
    public CollisionHeightsResult SetCollisionHeights(IReadOnlyList<ArchetypeHeight> heights, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(heights);
        return session.EditCatalogFiles(catalogs => Write(catalogs, heights, overwrite));
    }

    static CollisionHeightsResult Write(TileWorldCatalogs catalogs, IReadOnlyList<ArchetypeHeight> heights,
        bool overwrite)
    {
        var skipped = new List<CollisionHeightSkip>();
        var errors = new List<ArchetypeHeightError>();
        var pending = new List<(ArchetypeHeight Entry, string File, float? Previous)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ArchetypeHeight entry in heights)
        {
            string id = entry?.Id ?? "";
            if (entry is null || id.Length == 0)
            {
                errors.Add(new ArchetypeHeightError(id, "an entry needs an archetype id."));
                continue;
            }
            float h = entry.Height;
            if (!seen.Add(id))
            {
                errors.Add(new ArchetypeHeightError(id, "listed more than once, only the first is used."));
                continue;
            }
            if (!(float.IsFinite(h) && h > 0f))
            {
                errors.Add(new ArchetypeHeightError(id,
                    $"collisionHeight {h.ToString(CultureInfo.InvariantCulture)} is not a finite number above 0."));
                continue;
            }
            if (catalogs.Archetype(id) is not { } archetype)
            {
                errors.Add(new ArchetypeHeightError(id, "the open catalogs have not defined this archetype."));
                continue;
            }
            if (catalogs.ArchetypeSource(id) is not { } file)
            {
                errors.Add(new ArchetypeHeightError(id, "this archetype did not come from a catalog file."));
                continue;
            }
            if (archetype.CollisionHeight is { } recorded && (!overwrite || recorded == h))
            {
                skipped.Add(new CollisionHeightSkip(id, recorded, h, recorded == h
                    ? "already records this height."
                    : "already records a height, pass overwrite to replace it."));
                continue;
            }
            pending.Add((entry, file, archetype.CollisionHeight));
        }

        // Every file is prepared, and checked to load, before any is written. A file that cannot take its edit
        // turns its own entries into errors and is not written, and the other files still are.
        var prepared = new List<(string File, byte[] Bytes)>();
        var failedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (IGrouping<string, (ArchetypeHeight Entry, string File, float? Previous)> group in pending.GroupBy(p => p.File))
        {
            try
            {
                byte[]? bytes = CatalogWriter.Prepare(group.Key,
                    group.ToDictionary(p => p.Entry.Id, p => p.Entry.Height, StringComparer.Ordinal));
                if (bytes is not null) prepared.Add((group.Key, bytes));
            }
            catch (TileWorldException ex)
            {
                failedFiles.Add(group.Key);
                foreach ((ArchetypeHeight entry, _, _) in group) errors.Add(new ArchetypeHeightError(entry.Id, ex.Message));
            }
        }
        foreach ((string file, byte[] bytes) in prepared) File.WriteAllBytes(file, bytes);

        CollisionHeightChange[] changed = pending
            .Where(p => !failedFiles.Contains(p.File))
            .Select(p => new CollisionHeightChange(p.Entry.Id, p.File, p.Previous, p.Entry.Height))
            .ToArray();
        return new CollisionHeightsResult(changed, skipped, errors);
    }

    // Why an archetype measured to nothing, naming its mesh reference and where it was looked for.
    static string MissingMesh(GltfMeshResolver resolver, TileObjectArchetype a, string? log)
    {
        if (string.IsNullOrWhiteSpace(a.MeshRef)) return "the archetype names no meshRef.";
        string path;
        try { path = resolver.PathFor(a.MeshRef); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"no mesh: '{a.MeshRef}' is not a usable path ({ex.Message}).";
        }
        if (!File.Exists(path)) return $"no mesh: '{a.MeshRef}' is not at {path}.";
        return log is null
            ? $"no mesh: '{a.MeshRef}' at {path} has no geometry."
            : $"no mesh: '{a.MeshRef}' at {path} did not load. {log}";
    }
}
