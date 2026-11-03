using System;
using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// One explicit checkout owns manifest bytes, authorization Git facts and source-file recipe inputs.
internal sealed class RendererPerfCheckout
{
    readonly string _root;

    internal RendererPerfCheckout(string root)
    {
        Require(Path.IsPathFullyQualified(root), RendererPerfEntrypointRefusal.CheckoutRootNotAbsolute, "GITHUB_WORKSPACE must name an absolute checkout root.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Require(SamePath(Git("rev-parse", "--show-toplevel"), _root), RendererPerfEntrypointRefusal.CheckoutRootNotGitTopLevel,
            "Checkout root must be the explicit Git top level.");
    }

    internal string ManifestPath(string unit) => Path.GetFullPath($"tools/Renderer.Perf.Tests/authorized/{unit}.json", _root);
    internal string Git(params string[] arguments) => RendererPerfHostProcess.Run("git", ["-C", _root, .. arguments]);

    internal void ValidateRecipePaths(RendererPerfManifest manifest)
    {
        foreach (var recipe in manifest.Recipes)
        {
            string id = RendererPerfRecipe.IdFromVersion(recipe.Version);
            Require(SamePath(RendererPerfRecipe.RecipePath(id),
                Path.Combine(_root, "tools/Renderer.Perf.Tooling.Tests/Gpu/RendererPerf/recipes", id + ".json")),
                RendererPerfEntrypointRefusal.RecipeSourceOutsideCheckout, "Recipe sources must belong to the authorized checkout root.");
        }
    }

    static bool SamePath(string actual, string expected) => Path.IsPathFullyQualified(actual)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    static void Require(bool condition, RendererPerfEntrypointRefusal refusal, string reason)
        => RendererPerfEntrypoint.Require(condition, refusal, reason);
}
