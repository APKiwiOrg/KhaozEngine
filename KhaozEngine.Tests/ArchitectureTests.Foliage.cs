using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    // Short-name prefixes foliage generation may never reach: physics and its backends, the renderers and GPU stack,
    // the legacy tile world, the editors and the game runtime.
    static readonly string[] FoliageForbidden =
        ["Physics", "Render", "Gpu", "Windowing", "Gui", "TileWorld", "MapEdit", "Game", "Particles", "Audio"];

    [Fact]
    public void FoliageRuntime_HasNoPhysicsRenderGpuTileWorldDependency()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        // Foliage payloads, contexts, generation, comparison, edits and effects all live in MapDoc. Its direct production
        // edges and their whole transitive closure reach none of the forbidden packages.
        string[] direct = graph["KhaozEngine.MapDoc"].ProjectRefs.Select(Short).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Content", "Primitives", "Serialization", "Terrain"], direct);
        string[] closure = TransitiveClosure("KhaozEngine.MapDoc", graph).Select(Short).ToArray();
        Assert.Contains("Terrain", closure);
        Assert.DoesNotContain(closure, name => FoliageForbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
        string[] packages = TransitiveClosure("KhaozEngine.MapDoc", graph).Append("KhaozEngine.MapDoc").Where(graph.ContainsKey)
            .SelectMany(name => graph[name].PackageRefs).Where(p => !IgnoredInfraPackages.Contains(p)).Distinct().ToArray();
        Assert.DoesNotContain(packages, p => ThirdPartyHomes.TryGetValue(p, out string[]? homes) &&
            homes.Any(home => FoliageForbidden.Any(prefix => home.StartsWith(prefix, StringComparison.Ordinal))));

        // The CPU kernel foliage shares with the legacy distributor stays below every renderer as well.
        Assert.DoesNotContain(TransitiveClosure("KhaozEngine.Terrain", graph).Select(Short),
            name => FoliageForbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

        // The collider builder for MapDoc worlds is MapDoc.Physics. It reaches physics through the seam only and no
        // renderer, GPU, tile world or editor, and the editor never reaches it. Its compiled metadata holds no foliage
        // type, which MapFoliageWorldTests reads from the built assembly.
        string[] colliders = TransitiveClosure("KhaozEngine.MapDoc.Physics", graph).Select(Short).ToArray();
        Assert.Contains("Physics", colliders);
        Assert.DoesNotContain(colliders, name => name.StartsWith("Physics.", StringComparison.Ordinal) || name.StartsWith("Render", StringComparison.Ordinal) ||
            name.StartsWith("Gpu", StringComparison.Ordinal) || name.StartsWith("TileWorld", StringComparison.Ordinal) || name.StartsWith("MapEdit", StringComparison.Ordinal));
        Assert.DoesNotContain("KhaozEngine.MapDoc.Physics", TransitiveClosure("KhaozEngine.MapEditor", graph));

        // Every packable library that references MapDoc directly and also reaches physics is a deliberate entry here, so a
        // new direct consumer that could turn generated samples into colliders is a visible edit.
        string[] combined = graph.Values.Where(p => p.IsPackableLibrary && p.ProjectRefs.Contains("KhaozEngine.MapDoc"))
            .Where(p => TransitiveClosure(p.Name, graph).Contains("KhaozEngine.Physics"))
            .Select(p => Short(p.Name)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(CombinedMapDocPhysics.SequenceEqual(combined), "direct MapDoc consumers with a physics edge: " + string.Join(", ", combined));
    }

    // The Foundation umbrella is code free. Dungeon, the editor and the terrain renderer consume MapDoc for authoring and
    // terrain, and MapDoc.Physics builds the statics, from placements and terrain only.
    static readonly string[] CombinedMapDocPhysics = ["Dungeon", "Foundation", "MapDoc.Physics", "MapEditor", "Terrain.Render3D"];
}
