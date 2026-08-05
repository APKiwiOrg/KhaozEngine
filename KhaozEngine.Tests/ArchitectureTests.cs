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
        // The engine-owned native Direct3D11 backend. Opt-in for the usual pay-for-what-you-use reason and for a
        // second one specific to it: welding a Direct3D backend into a graph every consumer carries would make
        // the D3D11 interop non-optional for the Linux server heads (decision P1 of
        // docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md).
        "Gpu.D3D11",
        // The engine-owned native Vulkan backend, opt-in for the same two reasons (decision V-P1 of
        // docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md). Silk.NET.Vulkan is a large assembly, and the
        // premise of this phase is Veldrid leaving rather than dependency count falling, so a consumer that
        // never names Vulkan must never load it.
        "Gpu.Vulkan",
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
        // Veldrid's own D3D11 binding, already transitive via Veldrid. Declared in Gpu for the driver-threading
        // probe (Internal/D3D11ThreadingProbe), and in Gpu.D3D11 because that package IS the Direct3D11 interop.
        // Two homes, one binding: both pin the same Vortice 2.3.0 line, which is what Veldrid depends on, so
        // there is exactly one D3D11 binding and one SharpGen.Runtime in the graph.
        ["Vortice.Direct3D11"] = new[] { "Gpu", "Gpu.D3D11" },
        // FXC, for the native backend's own D3DCompile call. Only the backend compiles shaders to DXBC.
        ["Vortice.D3DCompiler"] = new[] { "Gpu.D3D11" },
        // Windowing / input: only AppWindow (KhaozEngine.Windowing) touches Silk.NET / GLFW.
        ["Silk.NET.Windowing"] = new[] { "Windowing" },
        ["Silk.NET.Windowing.Glfw"] = new[] { "Windowing" },
        ["Silk.NET.Input"] = new[] { "Windowing" },
        ["Silk.NET.Input.Glfw"] = new[] { "Windowing" },
        ["Silk.NET.GLFW"] = new[] { "Windowing" },
        // The Vulkan binding (decision V-P2), contained in the native Vulkan backend package and nowhere else.
        // Same Silk.NET 2.23.0 line as the windowing and audio stacks, which is the whole argument for taking
        // it: one vendor, one Silk.NET.Core, one lockstep upgrade. The two extension assemblies are separate
        // packages in this binding rather than members of the core one, so all three are mapped.
        ["Silk.NET.Vulkan"] = new[] { "Gpu.Vulkan" },
        ["Silk.NET.Vulkan.Extensions.KHR"] = new[] { "Gpu.Vulkan" },
        ["Silk.NET.Vulkan.Extensions.EXT"] = new[] { "Gpu.Vulkan" },
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
    public void Simulation_ReferencesOnlyDeterminism()
    {
        // Simulation was a zero-dependency leaf until 16.12.0, when ThreadPoolJobScheduler took on pinning the
        // canonical FP environment around each worker body. DeterministicFp pins the FP control register per THREAD,
        // and the whole point of that scheduler is to run sim work on threads that are neither the caller's nor a
        // dedicated sim thread, so the determinism primitive belongs at the scheduling boundary rather than at every
        // consumer call site. The guard is kept and narrowed rather than deleted: exactly one edge is allowed, so a
        // future reference still fails here.
        IReadOnlySet<string> refs = LoadGraph()["KhaozEngine.Simulation"].ProjectRefs;
        string[] unexpected = refs.Select(Short).Where(r => r != "Determinism").ToArray();
        bool clean = unexpected.Length == 0;
        Assert.True(clean, "Simulation sits at the bottom of the server/netcode stack and may reference only "
            + "KhaozEngine.Determinism, but also references: " + string.Join(", ", unexpected));
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

    /// <summary>
    /// Decisions P2 (Direct3D 11) and V-P3 (Vulkan): an engine-owned native backend declares NO Veldrid package
    /// of its own. Both shader paths need SPIRV-Cross, which ships as <c>Veldrid.SPIRV</c>, and the tempting
    /// shortcut is to reference it straight from the backend and bless the edge above. That is rejected:
    /// blessing a Veldrid package inside a backend whose entire premise is being Veldrid-free is a bad signal
    /// no other guard would ever catch, and it would scatter the eventual SPIRV-Cross replacement across
    /// several packages instead of one. The edge stays in <c>KhaozEngine.Gpu</c> behind an internal,
    /// Veldrid-free cross-compile helper (<c>Internal/SpirvCrossCompile</c>) plus <c>InternalsVisibleTo</c>.
    /// <para>
    /// What is asserted is the DECLARED edge, which is the one a person adds. Veldrid is of course still in each
    /// backend's transitive closure, through <c>KhaozEngine.Gpu</c>, and must be: that is where the helper lives.
    /// The property that actually matters is that no Veldrid TYPE is reachable from a backend's IL, and a
    /// project-file scan cannot see types, so <c>GpuPublicApiTests</c> asserts that half by reflecting over the
    /// built assemblies' references. The two together are the guard, and the IL walk is the load-bearing half.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("KhaozEngine.Gpu.D3D11")]
    [InlineData("KhaozEngine.Gpu.Vulkan")]
    public void NativeGpuBackend_DeclaresNoVeldridPackage(string backendProject)
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        Project backend = graph[backendProject];

        string[] veldrid = backend.PackageRefs
            .Where(p => p.StartsWith("Veldrid", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        bool clean = veldrid.Length == 0;
        Assert.True(clean,
            backendProject + " declares a Veldrid PackageReference: [" + string.Join(", ", veldrid) + "]. " +
            "A native backend is Veldrid-free by construction. If this was added for the SPIRV-Cross shader " +
            "path, put the call behind KhaozEngine.Gpu's internal SpirvCrossCompile helper instead, whose " +
            "signatures are Veldrid-free precisely so this edge never has to exist.");
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
