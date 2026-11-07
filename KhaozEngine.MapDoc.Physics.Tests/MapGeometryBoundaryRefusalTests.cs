using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapGeometryBoundaryRefusalTests
{
    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("Z")]
    [InlineData("Scale")]
    [InlineData("ScaleUnderflow")]
    public void BackendPose_IdentifiesTheUnrepresentableComponent(string field)
    {
        double large = (double)float.MaxValue * 2;
        var pose = new MapEffectiveTransform(field == "X" ? large : 0, field == "Y" ? large : 0,
            field == "Z" ? large : 0, 0, field == "Scale" ? large : field == "ScaleUnderflow" ? Math.ScaleB(1, -200) : 1);
        var error = Assert.Throws<MapDocumentException>(() => MapBackendPoseAdapter.Pose(pose, default));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("Pose", error.Operation);
        Assert.Equal(field.StartsWith("Scale", StringComparison.Ordinal) ? "Scale" : "Position." + field, error.FieldPath);
        Assert.Equal(field == "ScaleUnderflow" ? Math.ScaleB(1, -200) : large, error.ActualNumber);
        // The refused underflow value is normal binary64. Its control is normal binary32 on every platform.
        var control = field switch
        {
            "X" => pose with { X = float.MaxValue },
            "Y" => pose with { Y = float.MaxValue },
            "Z" => pose with { Z = float.MaxValue },
            "Scale" => pose with { Scale = float.MaxValue },
            _ => pose with { Scale = Math.ScaleB(1, -126) },
        };
        Vector3 position = MapBackendPoseAdapter.Pose(control, default).Position;
        Assert.True(float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z));
    }

    [Fact]
    public void BackendErrorBound_ReportsTheUnrepresentableMagnitude()
    {
        var pose = new MapEffectiveTransform(0, 0, 0, 0, 1);
        var error = Assert.Throws<MapDocumentException>(() => MapBackendPoseAdapter.ErrorMetres(pose, default, double.MaxValue));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("ErrorMetres", error.Operation);
        Assert.Equal("HalfUlp", error.FieldPath);
        Assert.Equal(double.MaxValue, error.ActualNumber);
        Assert.True(double.IsFinite(MapBackendPoseAdapter.ErrorMetres(pose, default, 1)));
    }

    [Fact]
    public void SweptEnvelope_IdentifiesTheUnsupportedShape()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapInteractionEnvelope.Build("small-sphere", new SphereShape(0.1f), true, Pose.At(Vector3.Zero)));
        Assert.Equal(MapDocumentRefusal.PhysicsSweepShapeUnsupported, error.Reason);
        Assert.Equal("small-sphere", error.PlacementId);
        Assert.Equal(typeof(SphereShape), error.ShapeType);
        Assert.True(MapInteractionEnvelope.Build("small-sphere", new BoxShape(new(0.1f)), true, Pose.At(Vector3.Zero)).RaiseMetres > 0);
    }

    [Fact]
    public void EnvelopeCapacity_IdentifiesTheOwnerAndAcceptsTheCapAndOneBelow()
    {
        // Each axis reaches 2^18 + 1 cells. Their exact odd product exceeds binary64 integer precision.
        const long cells = (1 << 18) + 1;
        double max = cells * 16 - 1;
        var bounds = new MapBox3(1, 1, 1, max, max, max);
        var error = Assert.Throws<MapDocumentException>(() => new MapEnvelopeIndex([bounds], ["wide-envelope"]));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("interaction envelope index cells", error.Budget);
        Assert.Equal("wide-envelope", error.OwnerId);
        Assert.Equal(cells * cells * cells, error.ActualCount);
        Assert.Equal(MapEnvelopeIndex.MaxCellsPerEnvelope, error.Limit);
        Assert.NotEmpty(Index(new(1, 1, 1, 16383, 2, 16383)));
        // A one-cell-high, one-cell-deep strip makes the exact cap-minus-one count representable.
        Assert.NotEmpty(Index(new(1, 1, 1, (MapEnvelopeIndex.MaxCellsPerEnvelope - 1) * 16 - 1, 2, 2)));
    }

    static int[] Index(MapBox3 bounds) => new MapEnvelopeIndex([bounds], ["wide-envelope"])
        .Cells(new(1, 1, 1), new(1, 0, 0), 0, 1).First().Envelopes;
}
