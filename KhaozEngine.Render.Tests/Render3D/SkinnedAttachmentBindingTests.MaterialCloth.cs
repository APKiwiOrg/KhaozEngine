using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class SkinnedAttachmentBindingTests
{
    [Fact]
    public void Material_overrides_resolve_through_the_same_source_ordinals_as_the_fitted_mesh()
    {
        using var rig = new OutfitRig();
        var fixture = ClothAsset(rig);
        ClothSurfaceFile fitted = ClothSurfaceJson.Parse(fixture.Bytes, ClothKey);
        var source = Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(fixture.Set.Parts));
        var recipe = Assert.IsType<ClothMotionDefinition>(source.Motion);
        ClothRenderEmbedding moving = fitted.Render[0];
        ClothRenderEmbedding material = moving with { Barycentric = Vector3.UnitZ, SignedOffset = -SkinOffset };
        var file = fitted with { MaterialRender = [material] };
        Assert.Equal(new ClothRoundTrip(0, 0),
            ClothSurfaceBinding.Check(rig.Source.Bytes[source.Mesh.Key], file, recipe.Fallback, ClothKey, ClothPartId));
        var surface = BindMaterialCloth(rig, fixture.Set, source, recipe, file).ClothSurface(ClothPartId)!;
        Assert.True(surface.HasMaterialMapping);
        var view = surface.Materials[1];
        Assert.Equal(moving.Barycentric, view.Embeddings[0].Barycentric);
        Assert.Equal(material.Barycentric, view.MaterialEmbeddings[0].Barycentric);
        Assert.Equal(material.SignedOffset, view.MaterialEmbeddings[0].SignedOffset);
        Assert.Equal(view.Embeddings[0].VertexIndex, view.MaterialEmbeddings[0].VertexIndex);
        Assert.Equal(view.Embeddings[1].Barycentric, view.MaterialEmbeddings[1].Barycentric);
        Assert.Equal(view.Embeddings[1].SignedOffset, view.MaterialEmbeddings[1].SignedOffset);
        Assert.Equal(surface.Materials[0].Embeddings.Length, surface.Materials[0].MaterialEmbeddings.Length);

        var error = Assert.Throws<ClothSurfaceDataException>(() => ClothSurfaceBinding.Check(
            rig.Source.Bytes[source.Mesh.Key], file with { Render = fitted.Render.SetItem(0, material) },
            recipe.Fallback, ClothKey, ClothPartId));
        Assert.Equal(ClothSurfaceRefusal.RoundTrip, error.Reason);
        Assert.Equal("$.render[0]", error.JsonPath);
    }

    internal static BoundAttachmentSet BindMaterialCloth(OutfitRig rig, AttachmentSetDefinition original,
        SkinnedAttachmentPartDefinition source, ClothMotionDefinition recipe, ClothSurfaceFile file)
    {
        const string key = "material-coat.kecs";
        string hash = rig.Source.Add(key, ClothSurfaceJson.Write(file));
        var part = source with { Motion = new ClothMotionDefinition(new(key, hash), recipe.Fallback) };
        return rig.Wear("material-coat.json", new AttachmentSetDefinition([part], original.Layer!.Value, [], original.Colliders));
    }
}
