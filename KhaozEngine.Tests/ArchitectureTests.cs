using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Mechanical enforcement of the engine's dependency-graph rules, which otherwise live only in prose
/// (docs/DEPENDENCY-SEAMS.md and the AGENTS.md "Dependency layering" section). These read the real
/// <c>*.csproj</c> files, so a ProjectReference / PackageReference edit that breaks a documented seam,
/// a layering invariant, or an umbrella's membership fails CI instead of silently drifting from the docs.
/// The graph is pure XML parsing, no build or GPU needed.
/// </summary>
public class ArchitectureTests
{
    // The four code-free umbrella metapackages. A package is "in an umbrella" iff it is in the umbrella's
    // transitive ProjectReference closure.
    static readonly string[] Umbrellas =
    {
        "KhaozEngine.Foundation", "KhaozEngine.Game2D", "KhaozEngine.Game3D", "KhaozEngine.Server",
    };

    // Opt-in backends (AGENTS.md "in NO umbrella" list + the Commerce SQL backends from DEPENDENCY-SEAMS.md,
    // plus the Identity.Oidc / Identity.Discord opt-in provider packages README.md and DEPENDENCY-SEAMS.md
    // document as "add explicitly like Physics.Bepu"). Pay-for-what-you-use: a consumer that does not want the
    // heavy/platform-specific dependency must not drag it in transitively through any umbrella. Short names
    // (KhaozEngine. prefix stripped).
    static readonly string[] OptInBackends =
    {
        "Physics.Bepu", "WorldStore.Sqlite", "WorldStore.SqlServer",
        "Server.Admin", "Social.Discord", "Commerce.Sqlite", "Commerce.SqlServer",
        "Identity.Oidc", "Identity.Discord",
    };

    // The GPU / runtime stack. None of these may appear in the Foundation or Server umbrella closures (both
    // are documented GPU-free metapackages: the GPU-free foundation, and the headless no-GPU sim server).
    // Short names.
    static readonly string[] GpuRuntimeStack =
    {
        "Gpu", "Windowing", "Render2D", "Render3D", "Gui", "Audio", "Particles", "Telegraphs",
    };

    // Third-party package id -> the engine packages (short names) allowed to reference it, straight from
    // docs/DEPENDENCY-SEAMS.md. Two directions are enforced against this map (see the two containment tests):
    // a listed package may only be referenced by a home in its set, and every third-party PackageReference
    // must appear here (or in IgnoredInfraPackages) so adding a dependency is always a deliberate edit.
    static readonly Dictionary<string, string[]> ThirdPartyHomes = new(StringComparer.Ordinal)
    {
        // GPU seam: the Veldrid binding is contained inside KhaozEngine.Gpu (Internal/VeldridGpuDevice).
        ["Veldrid"] = new[] { "Gpu" },
        ["Veldrid.SPIRV"] = new[] { "Gpu" },
        // Pinned alongside Veldrid.SPIRV shader reflection, stays inside Gpu.
        ["Newtonsoft.Json"] = new[] { "Gpu" },
        // Windowing / input: only AppWindow (KhaozEngine.Windowing) touches Silk.NET / GLFW.
        ["Silk.NET.Windowing"] = new[] { "Windowing" },
        ["Silk.NET.Windowing.Glfw"] = new[] { "Windowing" },
        ["Silk.NET.Input"] = new[] { "Windowing" },
        ["Silk.NET.Input.Glfw"] = new[] { "Windowing" },
        ["Silk.NET.GLFW"] = new[] { "Windowing" },
        // Audio backend: OpenAL plus the ogg / mp3 decoders, all contained in KhaozEngine.Audio.
        ["Silk.NET.OpenAL"] = new[] { "Audio" },
        ["Silk.NET.OpenAL.Soft.Native"] = new[] { "Audio" },
        ["NVorbis"] = new[] { "Audio" },
        ["NLayer"] = new[] { "Audio" },
        // 3D physics seam backend.
        ["BepuPhysics"] = new[] { "Physics.Bepu" },
        // Netcode transport seam backend.
        ["LiteNetLib"] = new[] { "Netcode.LiteNetLib" },
        // Persistence + commerce SQL backends. Managed provider plus the bundled native sqlite engine.
        ["Microsoft.Data.Sqlite"] = new[] { "WorldStore.Sqlite", "Commerce.Sqlite" },
        ["SQLitePCLRaw.lib.e_sqlite3"] = new[] { "WorldStore.Sqlite", "Commerce.Sqlite" },
        ["Microsoft.Data.SqlClient"] = new[] { "WorldStore.SqlServer", "Commerce.SqlServer" },
        // glTF load contained in Render3D's GltfLoader.
        ["SharpGLTF.Core"] = new[] { "Render3D" },
        // Image + font decode contained in Render2D (ImageRgba / SpriteFont).
        ["StbImageSharp"] = new[] { "Render2D" },
        ["StbTrueTypeSharp"] = new[] { "Render2D" },
        // Content validation contained in KhaozEngine.Content (JsonSchemaValidator).
        ["JsonSchema.Net"] = new[] { "Content" },
        // OIDC identity backend.
        ["Microsoft.IdentityModel.Protocols.OpenIdConnect"] = new[] { "Identity.Oidc" },
        ["Microsoft.IdentityModel.JsonWebTokens"] = new[] { "Identity.Oidc" },
        // The localization analyzer and the file-size ratchet analyzer are both Roslyn analyzers
        // (netstandard2.0), so both carry Roslyn.
        ["Microsoft.CodeAnalysis.CSharp"] = new[] { "Localization.Analyzers", "CodeHealth.Analyzers" },
    };

    // Benign build-infrastructure packages that any package may carry. Listed, not silently skipped, so the
    // completeness check stays honest. SourceLink is injected globally by Directory.Build.props for packable
    // projects, so it does not appear in a per-project scan today, but naming it keeps the intent explicit.
    static readonly HashSet<string> IgnoredInfraPackages = new(StringComparer.Ordinal)
    {
        "Microsoft.SourceLink.GitHub",
    };

    // The only packages multi-targeted below the repo-wide single <TargetFramework>, and the exact set each
    // must carry. KhaozEngine.ServerStatus plus its full ProjectReference chain (Diagnostics, Primitives) ship
    // a net8.0 lib alongside net10.0 so an Azure Functions isolated-worker app on the Linux Consumption (Y1)
    // plan can reference them. KhaozEngine.Http joins them on its own (it has no ProjectReference chain to
    // carry along, being a zero-dependency leaf): the same bounded-retry helper is exactly what a Functions
    // consumer also wants. Linux Consumption does not support .NET 10 (its newest supported LTS is .NET 8),
    // so dropping net8.0 would silently break those Functions consumers, and adding a second TFM to any other
    // package would bloat the fleet for no reason. Both directions are pinned here: the named packages must
    // carry exactly this set, and no other project may declare a plural <TargetFrameworks> at all.
    static readonly Dictionary<string, string[]> MultiTargetedPackages = new(StringComparer.Ordinal)
    {
        ["KhaozEngine.ServerStatus"] = new[] { "net8.0", "net10.0" },
        ["KhaozEngine.Diagnostics"] = new[] { "net8.0", "net10.0" },
        ["KhaozEngine.Primitives"] = new[] { "net8.0", "net10.0" },
        ["KhaozEngine.Http"] = new[] { "net8.0", "net10.0" },
    };

    [Fact]
    public void ThirdPartyPackages_StayInTheirSeamOrBackendHome()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        var violations = new List<string>();
        foreach (Project p in graph.Values.Where(p => p.IsPackableLibrary))
        {
            foreach (string pkg in p.PackageRefs)
            {
                if (IgnoredInfraPackages.Contains(pkg)) continue;
                if (ThirdPartyHomes.TryGetValue(pkg, out string[]? homes) && !homes.Contains(Short(p.Name)))
                    violations.Add($"{Short(p.Name)} references {pkg}, contained to [{string.Join(", ", homes)}]");
            }
        }

        bool clean = violations.Count == 0;
        Assert.True(clean, "A third-party package escaped its documented seam/backend home: " + string.Join("; ", violations));
    }

    [Fact]
    public void EveryThirdPartyPackage_IsDeliberatelyMapped()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        var unmapped = new List<string>();
        foreach (Project p in graph.Values.Where(p => p.IsPackableLibrary))
            foreach (string pkg in p.PackageRefs)
                if (!IgnoredInfraPackages.Contains(pkg) && !ThirdPartyHomes.ContainsKey(pkg))
                    unmapped.Add($"{pkg} (in {Short(p.Name)})");

        bool clean = unmapped.Count == 0;
        Assert.True(clean,
            "A third-party PackageReference is not in the containment allowlist. Add it to ThirdPartyHomes mapped to " +
            "the engine package that owns it, or to IgnoredInfraPackages if it is benign build infrastructure: " +
            string.Join("; ", unmapped.Distinct()));
    }

    [Fact]
    public void MultiTargetedPackages_CarryTheirPinnedFrameworks_AndNoOtherPackageMultiTargets()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        var violations = new List<string>();
        foreach (Project p in graph.Values)
        {
            string[] actual = p.TargetFrameworks.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            if (MultiTargetedPackages.TryGetValue(p.Name, out string[]? expected))
            {
                string[] want = expected.OrderBy(s => s, StringComparer.Ordinal).ToArray();
                if (!want.SequenceEqual(actual, StringComparer.Ordinal))
                    violations.Add($"{Short(p.Name)} must multi-target [{string.Join(", ", want)}] but declares [{string.Join(", ", actual)}]");
            }
            else if (actual.Length > 0)
            {
                violations.Add(
                    $"{Short(p.Name)} declares <TargetFrameworks> [{string.Join(", ", actual)}] but is not in the multi-target " +
                    "allowlist. Every package except KhaozEngine.ServerStatus (+ its ProjectReference chain) and KhaozEngine.Http " +
                    "stays on the single repo-wide TargetFramework.");
            }
        }

        bool clean = violations.Count == 0;
        Assert.True(clean,
            "Multi-targeting drifted from the pinned set. KhaozEngine.ServerStatus and its ProjectReference chain (Diagnostics, " +
            "Primitives), plus the dependency-free KhaozEngine.Http, ship net8.0 alongside net10.0 so an Azure Functions app on " +
            "Linux Consumption (which has no .NET 10) can reference them. Keep that set exact: " + string.Join("; ", violations));
    }

    [Fact]
    public void Primitives_IsTheZeroDependencyLeaf()
    {
        IReadOnlySet<string> refs = LoadGraph()["KhaozEngine.Primitives"].ProjectRefs;
        bool leaf = refs.Count == 0;
        Assert.True(leaf, "Primitives is the zero-dependency leaf at the bottom of the graph but references: " + string.Join(", ", refs));
    }

    [Fact]
    public void Simulation_IsTheZeroDependencyLeaf()
    {
        IReadOnlySet<string> refs = LoadGraph()["KhaozEngine.Simulation"].ProjectRefs;
        bool leaf = refs.Count == 0;
        Assert.True(leaf, "Simulation is a zero-dependency leaf (the server/netcode stack layers on top of it) but references: " + string.Join(", ", refs));
    }

    [Fact]
    public void FoundationUmbrella_StaysGpuFree()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        HashSet<string> closure = TransitiveClosure("KhaozEngine.Foundation", graph).Select(Short).ToHashSet(StringComparer.Ordinal);
        string[] hits = GpuRuntimeStack.Where(closure.Contains).ToArray();

        bool clean = hits.Length == 0;
        Assert.True(clean, "Foundation is the GPU-free foundation but its ProjectReference closure pulls in the GPU/runtime stack: " + string.Join(", ", hits));
    }

    [Fact]
    public void ServerUmbrella_StaysGpuFree()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        HashSet<string> closure = TransitiveClosure("KhaozEngine.Server", graph).Select(Short).ToHashSet(StringComparer.Ordinal);
        string[] hits = GpuRuntimeStack.Where(closure.Contains).ToArray();

        bool clean = hits.Length == 0;
        Assert.True(clean, "Server is the headless no-GPU sim-server metapackage but its ProjectReference closure pulls in the GPU/runtime stack: " + string.Join(", ", hits));
    }

    [Fact]
    public void App_NeverReferencesGui()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        HashSet<string> closure = TransitiveClosure("KhaozEngine.App", graph).Select(Short).ToHashSet(StringComparer.Ordinal);

        bool cyclic = closure.Contains("Gui");
        Assert.False(cyclic, "App must never reference Gui. The localization sink edge runs Gui -> App, so App stays acyclic and Gui-free.");
    }

    [Theory]
    [MemberData(nameof(UmbrellaMembership))]
    public void UmbrellaMembership_MatchesTheLockedList(string umbrella, string[] expectedShort)
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        string[] actual = graph[umbrella].ProjectRefs.Select(Short).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        string[] expected = expectedShort.OrderBy(s => s, StringComparer.Ordinal).ToArray();

        bool same = expected.SequenceEqual(actual, StringComparer.Ordinal);
        Assert.True(same,
            $"{Short(umbrella)} umbrella membership changed. Expected [{string.Join(", ", expected)}] but found [{string.Join(", ", actual)}]. " +
            "Membership is locked here on purpose: change the expected list only when the umbrella really should gain or lose a package.");
    }

    // Expected ProjectReference set of each umbrella, taken from the current csproj files. A membership change
    // must edit this list, which is the visible, deliberate record the lock exists to force.
    public static TheoryData<string, string[]> UmbrellaMembership() => new()
    {
        {
            "KhaozEngine.Foundation",
            new[]
            {
                "App", "CodeHealth.Analyzers", "Collision", "Content", "Determinism", "Diagnostics", "Dungeon",
                "Ecs", "Http", "Identity", "Locomotion", "MapDoc", "Navigation", "Objectives", "Persistence",
                "Physics", "Platform", "Primitives", "Progression", "Serialization", "ServerStatus", "Social",
                "Stats", "Terrain", "Updates",
            }
        },
        {
            "KhaozEngine.Game2D",
            new[]
            {
                "Windowing", "Render2D", "Gui", "Audio", "Particles", "Telegraphs", "Game", "Foundation",
                "Localization.Analyzers", "CodeHealth.Analyzers",
            }
        },
        {
            "KhaozEngine.Game3D",
            new[]
            {
                "Game2D", "Render3D", "Game.Render3D", "Telegraphs.Render3D", "Terrain.Render3D",
                "Particles.Render3D", "Physics", "CodeHealth.Analyzers",
            }
        },
        {
            "KhaozEngine.Server",
            new[]
            {
                "Foundation", "Netcode", "Netcode.Abstractions", "Netcode.LiteNetLib", "Simulation",
                "Replication", "WorldStore", "Sharding", "NetWorld", "Physics", "CodeHealth.Analyzers",
            }
        },
    };

    [Fact]
    public void OptInBackends_AreNotReachableFromAnyUmbrella()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        var violations = new List<string>();
        foreach (string umbrella in Umbrellas)
        {
            HashSet<string> closure = TransitiveClosure(umbrella, graph).Select(Short).ToHashSet(StringComparer.Ordinal);
            foreach (string backend in OptInBackends.Where(closure.Contains))
                violations.Add($"{Short(umbrella)} pulls in {backend}");
        }

        bool clean = violations.Count == 0;
        Assert.True(clean, "Opt-in backends must stay out of every umbrella (pay-for-what-you-use): " + string.Join("; ", violations));
    }

    [Fact]
    public void Terrain_NeverReferencesRender3DOrPhysics()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        HashSet<string> closure = TransitiveClosure("KhaozEngine.Terrain", graph).Select(Short).ToHashSet(StringComparer.Ordinal);
        string[] hits = new[] { "Render3D", "Physics" }.Where(closure.Contains).ToArray();

        bool clean = hits.Length == 0;
        Assert.True(clean,
            "KhaozEngine.Terrain carries the render/physics-free streamer core (TerrainStreamer and friends) so a " +
            "headless server can reference it, but its ProjectReference closure pulls in: " + string.Join(", ", hits));
    }

    [Fact]
    public void Render3D_StaysSeamsOnly()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        HashSet<string> actual = graph["KhaozEngine.Render3D"].ProjectRefs.Select(Short).ToHashSet(StringComparer.Ordinal);

        // Render3D talks to simulation only through dependency-free seams (Collision, Physics), never a backend.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Ecs", "Windowing", "Gpu", "Primitives", "Render2D", "Collision", "Physics",
        };
        string[] extra = actual.Where(a => !allowed.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToArray();
        bool withinSeams = extra.Length == 0;
        Assert.True(withinSeams,
            $"Render3D gained ProjectReference(s) outside its seam set: [{string.Join(", ", extra)}]. " +
            "A new simulation-facing edge belongs behind a seam interface or in an adapter package, not directly on Render3D.");

        string[] backendEdges = actual.Where(OptInBackends.Contains).ToArray();
        bool noBackend = backendEdges.Length == 0;
        Assert.True(noBackend, "Render3D must never reference an opt-in backend package but references: " + string.Join(", ", backendEdges));
    }

    // A parsed engine project: its ProjectReference / PackageReference sets, whether it is a scan target
    // for third-party containment (a packable engine library, not a test, sample, or Exe tool), and the
    // frameworks it declares in a plural <TargetFrameworks> (empty when it inherits the single repo-wide
    // <TargetFramework> from Directory.Build.props).
    sealed record Project(
        string Name, bool IsPackableLibrary, IReadOnlySet<string> ProjectRefs, IReadOnlySet<string> PackageRefs,
        IReadOnlySet<string> TargetFrameworks);

    static string Short(string stem) =>
        stem.StartsWith("KhaozEngine.", StringComparison.Ordinal) ? stem["KhaozEngine.".Length..] : stem;

    // Repo tree located from this source file's compile-time path, so the graph is read regardless of the
    // test runner's working directory. Test projects are non-packable, so the deterministic-source pathmap
    // does not rewrite [CallerFilePath] here (same trick as CetCompatDefaultTests / GoldenCompare).
    static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    // Parses every <repo>/<dir>/<dir>.csproj (one level deep, so bin/obj is never walked) into the graph.
    static IReadOnlyDictionary<string, Project> LoadGraph()
    {
        var graph = new Dictionary<string, Project>(StringComparer.Ordinal);
        foreach (string dir in Directory.EnumerateDirectories(RepoRoot()))
        {
            foreach (string csproj in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(csproj);
                XElement root = XDocument.Load(csproj).Root!;

                bool hasPackageId = root.Descendants("PackageId").Any();
                bool nonPackable = root.Descendants("IsPackable").Any(e => string.Equals((string?)e, "false", StringComparison.OrdinalIgnoreCase));
                bool isExe = root.Descendants("OutputType").Any(e => string.Equals(((string?)e)?.Trim(), "Exe", StringComparison.OrdinalIgnoreCase));

                HashSet<string> projRefs = root.Descendants("ProjectReference")
                    .Select(e => (string?)e.Attribute("Include"))
                    .Where(s => s is not null)
                    .Select(s => Path.GetFileNameWithoutExtension(s!.Replace('\\', '/')))
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<string> pkgRefs = root.Descendants("PackageReference")
                    .Select(e => (string?)e.Attribute("Include"))
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<string> targetFrameworks = root.Descendants("TargetFrameworks")
                    .SelectMany(e => ((string?)e ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToHashSet(StringComparer.Ordinal);

                graph[name] = new Project(name, hasPackageId && !nonPackable && !isExe, projRefs, pkgRefs, targetFrameworks);
            }
        }
        return graph;
    }

    // Transitive ProjectReference closure of a node, excluding the node itself. The engine graph is acyclic.
    static HashSet<string> TransitiveClosure(string start, IReadOnlyDictionary<string, Project> graph)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            if (!graph.TryGetValue(cur, out Project? p)) continue;
            foreach (string dep in p.ProjectRefs)
                if (seen.Add(dep)) stack.Push(dep);
        }
        return seen;
    }
}
