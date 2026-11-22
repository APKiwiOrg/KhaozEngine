using System;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class LiftBasisTests
{
    const string Source = "part.glb";
    const string Direction = "_LIFT_DIRECTION";
    const string Bearing = "_LIFT_BEARING";

    [Theory]
    [InlineData(Direction, null, null)]
    [InlineData(Bearing, null, null)]
    [InlineData(Direction, "type", "SCALAR")]
    [InlineData(Bearing, "componentType", "5123")]
    [InlineData(Direction, "normalized", "true")]
    [InlineData(Bearing, "count", "2")]
    public void Each_primitive_requires_both_lift_attributes_in_the_expected_shape(
        string attribute, string? property, string? changedValue)
    {
        var fixture = SplitFixture();
        Assert.Equal(6, AttachmentGlb.LiftBasis(fixture.Build(), Source).Vertices.Count);

        JsonObject attributes = fixture.Primitives[1]!["attributes"]!.AsObject();
        if (property is null)
            attributes.Remove(attribute);
        else
        {
            int index = attributes[attribute]!.GetValue<int>();
            fixture.Accessors[index]![property] = property == "type" ? JsonValue.Create(changedValue) : JsonNode.Parse(changedValue!);
        }

        var error = Assert.Throws<AttachmentSkinnedGeometryDataException>(() => AttachmentGlb.LiftBasis(fixture.Build(), Source));
        Assert.Equal(property is null ? AttachmentSkinnedGeometryRefusal.LiftAttributeMissing
            : AttachmentSkinnedGeometryRefusal.LiftAttributeShapeMismatch, error.Reason);
        AssertLocation(error, 1, attribute);
    }

    [Theory]
    [InlineData(Direction, float.NaN)]
    [InlineData(Direction, 1.002f)]
    [InlineData(Bearing, float.PositiveInfinity)]
    [InlineData(Bearing, -.001f)]
    public void Invalid_lift_values_identify_the_attribute_and_vertex(string attribute, float value)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.SetLiftAttributes(0, [Vector3.UnitY, 1.0005f * Vector3.UnitZ, Vector3.UnitX], [0, .01f, .02f]);
        Assert.Equal(3, AttachmentGlb.LiftBasis(fixture.Build(), Source).Vertices.Count);

        int accessor = fixture.Primitives[0]!["attributes"]![attribute]!.GetValue<int>();
        if (attribute == Direction)
            fixture.SetElement(accessor, 1, 0, 0, value);
        else
            fixture.SetElement(accessor, 1, value);

        var error = Assert.Throws<AttachmentSkinnedGeometryDataException>(() => AttachmentGlb.LiftBasis(fixture.Build(), Source));
        Assert.Equal(attribute == Direction ? AttachmentSkinnedGeometryRefusal.LiftDirectionInvalid
            : AttachmentSkinnedGeometryRefusal.LiftBearingInvalid, error.Reason);
        AssertLocation(error, 0, attribute);
        Assert.Equal(1, error.VertexIndex);
    }

    [Theory]
    [InlineData(Direction, false)]
    [InlineData(Bearing, false)]
    [InlineData(Bearing, true)]
    public void Coincident_vertices_across_materials_identify_both_flattened_indices(string attribute, bool negativeZero)
    {
        var fixture = SplitFixture();
        if (negativeZero)
        {
            // Negative zero names the same point as zero, so the copy of (1, 0, 0) written as (1, -0, -0) is coincident.
            float minusZero = BitConverter.Int32BitsToSingle(int.MinValue);
            fixture.SetPositions(1, [Vector3.Zero, new(1, minusZero, minusZero), Vector3.UnitY]);
        }
        Assert.Equal(6, AttachmentGlb.LiftBasis(fixture.Build(), Source).Vertices.Count);

        int accessor = fixture.Primitives[1]!["attributes"]![attribute]!.GetValue<int>();
        if (attribute == Direction)
            fixture.SetElement(accessor, 1, 0, 0, 1);
        else
            fixture.SetElement(accessor, 1, .011f);

        var error = Assert.Throws<AttachmentSkinnedGeometryDataException>(() => AttachmentGlb.LiftBasis(fixture.Build(), Source));
        Assert.Equal(AttachmentSkinnedGeometryRefusal.CoincidentLiftDataMismatch, error.Reason);
        AssertLocation(error, 1, attribute);
        Assert.Equal(4, error.VertexIndex);
        Assert.Equal(1, error.OtherVertexIndex);
    }

    [Fact]
    public void Coincident_vertices_match_each_other_within_tolerance()
    {
        var fixture = SplitFixture();
        fixture.Primitives.Add(fixture.Primitives[1]!.DeepClone());
        fixture.SetLiftAttributes(1, [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [0, .010075f, .02f]);
        fixture.SetLiftAttributes(2, [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [0, .01f, .02f]);
        Assert.Equal(9, AttachmentGlb.LiftBasis(fixture.Build(), Source).Vertices.Count);

        int accessor = fixture.Primitives[2]!["attributes"]![Bearing]!.GetValue<int>();
        fixture.SetElement(accessor, 1, .009925f);

        var error = Assert.Throws<AttachmentSkinnedGeometryDataException>(() => AttachmentGlb.LiftBasis(fixture.Build(), Source));
        Assert.Equal(AttachmentSkinnedGeometryRefusal.CoincidentLiftDataMismatch, error.Reason);
        AssertLocation(error, 2, Bearing);
        Assert.Equal(7, error.VertexIndex);
        Assert.Equal(4, error.OtherVertexIndex);
    }

    [Fact]
    public void Strided_and_sparse_lift_data_reads_in_flattened_order()
    {
        var fixture = new SkinnedAttachmentGlbFixture(strided: true);
        fixture.SetLiftAttributes(0, [Vector3.UnitX, Vector3.UnitX, Vector3.UnitZ], [0, 0, .02f], strided: true);
        JsonNode attributes = fixture.Primitives[0]!["attributes"]!;
        fixture.SetSparse(attributes[Direction]!.GetValue<int>(), [1], [[0, 1, 0]]);
        fixture.SetSparse(attributes[Bearing]!.GetValue<int>(), [1], [[.01f]]);

        LiftBasis basis = AttachmentGlb.LiftBasis(fixture.Build(), Source);

        Assert.Collection(basis.Vertices,
            vertex => Assert.Equal(new LiftBasisVertex(Vector3.UnitX, 0, 0, 0), vertex),
            vertex => Assert.Equal(new LiftBasisVertex(Vector3.UnitY, .01f, 0, 1), vertex),
            vertex => Assert.Equal(new LiftBasisVertex(Vector3.UnitZ, .02f, 0, 2), vertex));
    }

    static SkinnedAttachmentGlbFixture SplitFixture()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        fixture.SetIndexedMaterialParts([0, 1, 2], 2);
        for (int primitive = 0; primitive < 2; primitive++)
            fixture.SetLiftAttributes(primitive, [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [0, .01f, .02f]);
        return fixture;
    }

    static void AssertLocation(AttachmentSkinnedGeometryDataException error, int primitive, string attribute)
    {
        Assert.Equal(Source, error.SourceName);
        Assert.Equal($"$.meshes[0].primitives[{primitive}].attributes.{attribute}", error.JsonPath);
        Assert.Equal(attribute, error.AttributeName);
    }
}
