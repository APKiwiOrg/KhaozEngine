using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The model material params block (issue #1776). Its C# mirror is one std140 vec4, and every shipped program
/// on the two model material layouts declares that block at the binding its layout gives it (set 0 binding 22 on the
/// rigid layout, set 1 binding 12 on the skinned fragment layout, docs/RENDER-PIPELINE.md), or the emissive colour is
/// read from the wrong bytes. The programs are found by the block they declare, one case each, and that set is held to
/// a roster written from the renderers, so a program that loses the block fails the roster instead of leaving the
/// validator unnoticed.</summary>
public sealed class MaterialParamsLayoutTests
{
    const string Block = "uniform MaterialParams { vec4 MaterialEmissive; };";
    const string RigidDeclaration = "layout(set=0, binding=22) " + Block;
    const string SkinnedDeclaration = "layout(set=1, binding=12) " + Block;

    /// <summary>Every program the model renderer builds on a material layout, with the declaration that layout
    /// expects: the rigid, foliage, dissolve, CPU-skinned and motion programs on the rigid layout and the GPU-skinned
    /// ones on the skinned layout, each with its shadow-visibility variant.</summary>
    static readonly Dictionary<string, string> Roster = BuildRoster();

    static Dictionary<string, string> BuildRoster()
    {
        string[] rigid =
        [
            "Model", "Foliage", "PreparedFoliage", "PreparedFoliageMotion", "ModelDissolve", "ModelMotion",
            "ModelCpuSkinnedMotion", "ModelCpuSkinnedDissolveMotion", "FoliageMotion",
        ];
        string[] skinned = ["SkinnedModel", "SkinnedModelDissolve", "SkinnedModelMotion", "SkinnedModelDissolveMotion"];
        var roster = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in rigid)
        {
            roster.Add(name, RigidDeclaration);
            roster.Add(name + "Visibility", RigidDeclaration);
        }
        foreach (string name in skinned)
        {
            roster.Add(name, SkinnedDeclaration);
            roster.Add(name + "Visibility", SkinnedDeclaration);
        }
        return roster;
    }

    static IEnumerable<ShippedGraphicsProgram> Declaring() => ShippedShaderPrograms.GraphicsPrograms()
        .Where(p => p.FragmentGlsl.Contains("uniform MaterialParams", StringComparison.Ordinal));

    public static TheoryData<string> DeclaringPrograms() => new(Declaring().Select(p => p.Name));

    [Fact]
    public void The_CSharp_mirror_is_one_std140_vec4()
    {
        Assert.Equal(16, Marshal.SizeOf<ModelRenderer.MaterialParamsUbo>());
        Assert.Equal((int)ModelRenderer.MaterialParamsUbo.SizeInBytes, Marshal.SizeOf<ModelRenderer.MaterialParamsUbo>());
        Assert.Equal(0, (int)Marshal.OffsetOf<ModelRenderer.MaterialParamsUbo>(nameof(ModelRenderer.MaterialParamsUbo.Emissive)));
    }

    [Fact]
    public void The_programs_declaring_the_block_are_the_material_layout_roster()
    {
        Assert.Equal(Roster.Keys.Order(StringComparer.Ordinal),
            Declaring().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DeclaringPrograms))]
    public void A_material_program_declares_the_block_at_its_layouts_binding(string name)
    {
        Assert.True(Roster.TryGetValue(name, out string? expected),
            $"{name} declares MaterialParams but is on neither model material layout");
        string fragment = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == name).FragmentGlsl;
        Assert.Contains(expected!, fragment, StringComparison.Ordinal);
    }
}
