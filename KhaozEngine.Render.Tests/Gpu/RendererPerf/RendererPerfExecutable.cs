using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfExecutable
{
    static readonly Lazy<SortedDictionary<string, string>> Sources = new(ShaderSourcesByName);
    internal static string ShaderSha256 => RendererPerfRecipe.Digest(Sources.Value);
    internal static IReadOnlyDictionary<string, string> ShaderHashes => Sources.Value;

    static SortedDictionary<string, string> ShaderSourcesByName()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(ShaderSources), typeof(SpriteBatch) })
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(string) && f.IsLiteral))
                result.Add(type.FullName + "." + field.Name, RendererPerfRecipe.Hash(Encoding.UTF8.GetBytes((string)field.GetRawConstantValue()!)));
        foreach (string name in new[] { "chain.comp", "stream.comp", "fma.comp", "dependent.comp", "fullscreen.vert", "fullscreen.frag", "depth-read.frag" })
            result.Add("control." + name, RendererPerfRecipe.Hash(Encoding.UTF8.GetBytes(RendererPerfControlShaders.Source(name))));
        return result;
    }

    internal static RendererPerfPassDefinition[] Layout(RendererPerfRecipeIdentity identity)
        => RendererPerfFrozenLayout.Create(identity);

    internal static void ValidateWork(RendererPerfManifest manifest)
    {
        foreach (var expected in manifest.Recipes)
        {
            using var recipe = RendererPerfRecipe.Create(expected.Version.Split('/')[0]);
            var f = recipe.Facts;
            var actual = new RendererPerfRecipeIdentity(expected.SceneId, f.Id + "/" + f.Version, f.Profile,
                f.RecipeSha256, f.SettingsSha256, ShaderSha256, f.GeometrySha256, f.AssetSha256);
            RendererPerfManifest.Require(actual == expected, "Identity", "The approved recipe, settings, shader, geometry or asset bytes changed.");
        }
        foreach (var control in manifest.Controls)
            RendererPerfManifest.Require(control.WorkSha256 == RendererPerfControls.Definition(control.Id).WorkSha256,
                "Identity", "The approved control definition or arithmetic seed bytes changed.");
    }
}
