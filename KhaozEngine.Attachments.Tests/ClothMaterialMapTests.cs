using System.Numerics;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class ClothMaterialMapTests
{
    private const string FittedOnly = """
        {"formatVersion":1,"meshSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sourceVertices":4,"sourceTriangles":2,"material":[1,3,4,5,1,0,0.01,0,0,0],"nodes":[[0,0,0,0,0],[1,0,0,1,0],[0,1,0,0,1],[1,1,0,1,1]],"triangles":[[0,1,2],[1,3,2]],"hinges":[],"pins":[[0,[0,0,0]]],"render":[[0,0,1,0,0,1,0,0.01],[0,0,3,1,0,1,0,-0.01]],"fallback":[[0,0,0],[0,0,1],[0,0,2],[0,0,3]]}
        """;

    [Fact]
    public void Material_version_writes_an_independent_sparse_map_and_preserves_fitted_bytes()
    {
        ClothSurfaceFile fitted = ClothSurfaceJson.Parse(Encoding.UTF8.GetBytes(FittedOnly), "coat.kecs");
        Assert.Equal(FittedOnly, Encoding.UTF8.GetString(ClothSurfaceJson.Write(fitted)));
        Assert.True(fitted.MaterialRender.IsDefault);
        ClothRenderEmbedding material = new(new(0, 0, 1), 1, new(.25f, .5f, .25f), -.02f);
        byte[] bytes = ClothSurfaceJson.Write(fitted with { MaterialRender = [material] });
        ClothSurfaceFile both = ClothSurfaceJson.Parse(bytes, "coat.kecs");
        Assert.Equal(material, Assert.Single(both.MaterialRender));
        Assert.Equal(fitted.Render.AsSpan().ToArray(), both.Render.AsSpan().ToArray());
        Assert.Equal(bytes, ClothSurfaceJson.Write(both));
        Assert.Equal(FittedOnly, Encoding.UTF8.GetString(ClothSurfaceJson.Write(both with { MaterialRender = default })));
    }

    [Theory]
    [InlineData(1, "[]", ClothSurfaceRefusal.Json, "$.materialRender", null)]
    [InlineData(2, null, ClothSurfaceRefusal.Json, "$.materialRender", null)]
    [InlineData(2, "null", ClothSurfaceRefusal.Json, "$.materialRender", null)]
    [InlineData(2, "[[0,0,1,1,0,1,0]]", ClothSurfaceRefusal.Json, "$.materialRender[0]", 0)]
    [InlineData(2, "[[0,0,2,1,0,1,0,0]]", ClothSurfaceRefusal.MaterialEmbedding, "$.materialRender[0]", 0)]
    [InlineData(2, "[[0,0,1,2,0,1,0,0]]", ClothSurfaceRefusal.MaterialEmbedding, "$.materialRender[0]", 0)]
    [InlineData(2, "[[0,0,1,1,0,0.9,0,0]]", ClothSurfaceRefusal.MaterialEmbedding, "$.materialRender[0]", 0)]
    [InlineData(2, "[[0,0,1,1,0,1,0,1e99]]", ClothSurfaceRefusal.MaterialEmbedding, "$.materialRender[0]", 0)]
    [InlineData(2, "[[0,0,1,1,0,1,0,0],[0,0,1,1,1,0,0,0]]", ClothSurfaceRefusal.MaterialRenderOrder, "$.materialRender[1]", 1)]
    [InlineData(2, "[[0,0,3,1,0,1,0,0],[0,0,1,1,1,0,0,0]]", ClothSurfaceRefusal.MaterialRenderOrder, "$.materialRender[1]", 1)]
    public void Material_version_refuses_invalid_correspondences_at_the_source_row(int version, string? rows,
        ClothSurfaceRefusal reason, string path, int? element)
    {
        string json = FittedOnly.Replace("\"formatVersion\":1", $"\"formatVersion\":{version}", System.StringComparison.Ordinal);
        if (rows is not null) json = json[..^1] + ",\"materialRender\":" + rows + "}";
        var error = Assert.Throws<ClothSurfaceDataException>(() => ClothSurfaceJson.Parse(Encoding.UTF8.GetBytes(json), "coat.kecs"));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("coat.kecs", error.SourceName);
        Assert.Equal(path, error.JsonPath);
        Assert.Equal(element, error.ElementIndex);
    }
}
