using System;
using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// One explicit checkout owns manifest bytes, authorization Git facts and source-file recipe inputs.
internal sealed class RendererPerfCheckout
{
    readonly string _root;

    internal RendererPerfCheckout(string root)
    {
        Require(Path.IsPathFullyQualified(root), "GITHUB_WORKSPACE must name an absolute checkout root.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Require(SamePath(Git("rev-parse", "--show-toplevel"), _root), "Checkout root must be the explicit Git top level.");
    }

    internal string ManifestPath(string unit) => Path.GetFullPath($"tools/Renderer.Perf.Tests/authorized/{unit}.json", _root);
    internal string Git(params string[] arguments) => RendererPerfHostProcess.Run("git", ["-C", _root, .. arguments]);

    internal void ValidateRecipePaths(RendererPerfManifest manifest)
    {
        foreach (var recipe in manifest.Recipes)
        {
            string id = recipe.Version.Split('/')[0];
            Require(SamePath(RendererPerfRecipe.RecipePath(id),
                Path.Combine(_root, "KhaozEngine.Render.Tests/Gpu/RendererPerf/recipes", id + ".json")),
                "Recipe sources must belong to the authorized checkout root.");
        }
    }

    static bool SamePath(string actual, string expected) => Path.IsPathFullyQualified(actual)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    static void Require(bool condition, string reason) => RendererPerfManifest.Require(condition, "Entrypoint", reason);
}
