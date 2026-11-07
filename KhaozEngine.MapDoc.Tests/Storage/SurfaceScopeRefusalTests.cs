using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceScopeRefusalTests
{
    [Theory]
    [InlineData("LocalMin.X", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("LocalMin.Y", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("LocalMax.X", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("LocalMax.Y", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("MinY", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("MaxY", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("XOrder", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("ZOrder", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("YOrder", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("Roles", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("Role", MapDocumentRefusal.SchemaEnumUnsupported)]
    [InlineData("SpaceIds", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("Limits", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("MaxCandidatePatches", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("MaxPageReads", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("MaxInspectedFaces", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("MaxSupportIntersections", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("MaxRecordReads", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("MaxRecordDepth", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Refinement.MaxSourceFacesPerBound", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Refinement.MaxPairChecks", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Refinement.MaxRefinementFaces", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Refinement.MaxRefinementVertices", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Refinement.MaxContextPatches", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Refinement.MaxContextFaces", MapDocumentRefusal.ValueOutsideRange)]
    public void ScopeAdmission_IdentifiesTheFailedBranchAndAcceptsItsBoundary(string field, MapDocumentRefusal reason)
    {
        var error = Assert.Throws<MapSurfaceScopeArgumentException>(() => Scope(field, repaired: false));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Null(error.ParamName);
        Assert.Equal(reason, error.Reason);
        string path = field switch
        {
            "XOrder" => "LocalMax.X",
            "ZOrder" => "LocalMax.Y",
            "YOrder" => "MaxY",
            "Role" => "Roles",
            _ when reason == MapDocumentRefusal.ValueOutsideRange => "Limits." + field,
            _ => field
        };
        Assert.Equal(path, error.FieldPath);
        if (reason == MapDocumentRefusal.ValueNonfinite) Assert.Equal(double.PositiveInfinity, error.ActualNumber);
        if (reason == MapDocumentRefusal.BoundsInvalid)
        {
            Assert.Equal(0, error.ActualNumber);
            Assert.Equal(1, error.MinimumNumber);
        }
        if (reason == MapDocumentRefusal.ValueOutsideRange)
        {
            Assert.Equal(-1, error.ActualInteger);
            Assert.Equal(0, error.MinimumInteger);
        }
        if (field == "Role")
        {
            Assert.Equal(typeof(MapSurfaceRole), error.EnumType);
            Assert.Equal(255, error.ActualInteger);
        }
        if (field == "SpaceIds") Assert.Equal("", error.ActualText);
        MapSurfaceScope control = Scope(field, repaired: true);
        Assert.NotEmpty(control.Digest);
    }

    [Fact]
    public void CopiedScope_IsReadmittedBeforeQuerying()
    {
        var control = Scope("MaxRecordDepth", repaired: true) with { Limits = new() };
        var source = MapDocumentSurfaceSource.Capture(SurfaceStorageFixtures.FlatPatches(1));
        var copied = control with { Limits = new(MaxRecordDepth: -1) };
        var error = Assert.Throws<MapSurfaceScopeArgumentException>(() => source.FindPatches(copied));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal("Limits.MaxRecordDepth", error.FieldPath);
        Assert.Equal(MapFindStatus.Complete, source.FindPatches(control).Status);
    }

    static MapSurfaceScope Scope(string field, bool repaired)
    {
        Vector2 min = Vector2.Zero, max = Vector2.Zero;
        float? low = 0, high = 0;
        var roles = new[] { MapSurfaceRole.SupportFloor };
        var spaces = new[] { "room" };
        var limits = new MapQueryLimits(0, 0, 0, 0, 0, 0);
        float nonfinite = repaired ? 0 : float.PositiveInfinity;
        int negative = repaired ? 0 : -1;
        switch (field)
        {
            case "LocalMin.X": min.X = nonfinite; break;
            case "LocalMin.Y": min.Y = nonfinite; break;
            case "LocalMax.X": max.X = nonfinite; break;
            case "LocalMax.Y": max.Y = nonfinite; break;
            case "MinY": low = nonfinite; break;
            case "MaxY": high = nonfinite; break;
            case "XOrder": min.X = 1; max.X = repaired ? 1 : 0; break;
            case "ZOrder": min.Y = 1; max.Y = repaired ? 1 : 0; break;
            case "YOrder": low = 1; high = repaired ? 1 : 0; break;
            case "Roles": if (!repaired) roles = null!; break;
            case "Role": if (!repaired) roles[0] = (MapSurfaceRole)255; break;
            case "SpaceIds": if (!repaired) spaces[0] = ""; break;
            case "Limits": if (!repaired) limits = null!; break;
            case "MaxCandidatePatches": limits = limits with { MaxCandidatePatches = negative }; break;
            case "MaxPageReads": limits = limits with { MaxPageReads = negative }; break;
            case "MaxInspectedFaces": limits = limits with { MaxInspectedFaces = negative }; break;
            case "MaxSupportIntersections": limits = limits with { MaxSupportIntersections = negative }; break;
            case "MaxRecordReads": limits = limits with { MaxRecordReads = negative }; break;
            case "MaxRecordDepth": limits = limits with { MaxRecordDepth = negative }; break;
            case "Refinement.MaxSourceFacesPerBound": limits = limits with { Refinement = new MapRefinementLimits() with { MaxSourceFacesPerBound = negative } }; break;
            case "Refinement.MaxPairChecks": limits = limits with { Refinement = new MapRefinementLimits() with { MaxPairChecks = negative } }; break;
            case "Refinement.MaxRefinementFaces": limits = limits with { Refinement = new MapRefinementLimits() with { MaxRefinementFaces = negative } }; break;
            case "Refinement.MaxRefinementVertices": limits = limits with { Refinement = new MapRefinementLimits() with { MaxRefinementVertices = negative } }; break;
            case "Refinement.MaxContextPatches": limits = limits with { Refinement = new MapRefinementLimits() with { MaxContextPatches = negative } }; break;
            case "Refinement.MaxContextFaces": limits = limits with { Refinement = new MapRefinementLimits() with { MaxContextFaces = negative } }; break;
        }
        return new(WorldFrame.Origin, min, max, low, high, roles, spaces, limits);
    }
}
