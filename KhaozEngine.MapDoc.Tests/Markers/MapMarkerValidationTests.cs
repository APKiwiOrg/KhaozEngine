using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerValidationTests
{
    [Theory]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "$.id")]
    [InlineData("name", MapDocumentRefusal.SchemaValueRequired, "$.name")]
    [InlineData("role", MapDocumentRefusal.SchemaValueRequired, "$.role")]
    [InlineData("x", MapDocumentRefusal.ValueNonfinite, "$.position.x")]
    [InlineData("z", MapDocumentRefusal.ValueNonfinite, "$.position.z")]
    [InlineData("yaw", MapDocumentRefusal.ValueNonfinite, "$.yawRadians")]
    [InlineData("tags", MapDocumentRefusal.SchemaValueRequired, "$.tags")]
    [InlineData("tag", MapDocumentRefusal.SchemaValueRequired, "$.tags[0]")]
    [InlineData("legacy-mode", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.heightMode")]
    [InlineData("legacy-binding", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.supportBinding")]
    [InlineData("legacy-archetype", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.archetypeId")]
    [InlineData("legacy-y", MapDocumentRefusal.ValueNonfinite, "$.position.y")]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "$.payloadVersion")]
    [InlineData("archetype", MapDocumentRefusal.SchemaValueRequired, "$.archetypeId")]
    [InlineData("explicit-binding", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.supportBinding")]
    [InlineData("explicit-y", MapDocumentRefusal.ValueNonfinite, "$.position.y")]
    [InlineData("support-y", MapDocumentRefusal.MarkerHeightChoiceInvalid, "$.position.y")]
    [InlineData("negative-zero", MapDocumentRefusal.MarkerHeightChoiceInvalid, "$.position.y")]
    [InlineData("support-nonfinite-y", MapDocumentRefusal.ValueNonfinite, "$.position.y")]
    [InlineData("support-binding", MapDocumentRefusal.SchemaValueRequired, "$.supportBinding")]
    [InlineData("height", MapDocumentRefusal.SchemaValueRequired, "$.heightMode")]
    [InlineData("enum", MapDocumentRefusal.SchemaEnumUnsupported, "$.heightMode")]
    public void LocalRules_KeepTheirTypedFindingAndProseProjection(string variant, MapDocumentRefusal reason, string field)
    {
        MapLocalMarkerDoc legacy = MarkerFixtures.BasicMarker();
        MapLocalMarkerDoc explicitPoint = MapMarkerHeights.Apply(legacy, new(float.MaxValue, null));
        MapLocalMarkerDoc support = MapMarkerHeights.Apply(legacy, MarkerFixtures.SurfaceHeight());
        MapLocalMarkerDoc control = variant.StartsWith("legacy-", StringComparison.Ordinal) ? legacy
            : variant.StartsWith("support-", StringComparison.Ordinal) || variant == "negative-zero" ? support : explicitPoint;
        control = variant switch
        {
            "x" => control with { Position = control.Position with { X = float.MaxValue } },
            "z" => control with { Position = control.Position with { Z = float.MaxValue } },
            "yaw" => control with { YawRadians = float.MaxValue },
            "legacy-y" => control with { Position = control.Position with { Y = float.MaxValue } },
            _ => control,
        };
        MapLocalMarkerDoc invalid = variant switch
        {
            "id" => control with { Id = " " },
            "name" => control with { Name = " " },
            "role" => control with { Role = " " },
            "x" => control with { Position = control.Position with { X = float.NaN } },
            "z" => control with { Position = control.Position with { Z = float.NaN } },
            "yaw" => control with { YawRadians = float.NaN },
            "tags" => control with { Tags = null! },
            "tag" => control with { Tags = [null!] },
            "legacy-mode" => control with { HeightMode = MapMarkerHeightMode.Explicit },
            "legacy-binding" or "explicit-binding" => control with { SupportBinding = support.SupportBinding },
            "legacy-archetype" => control with { ArchetypeId = "actor" },
            "version" => control with { PayloadVersion = 3 },
            "archetype" => control with { ArchetypeId = " " },
            "legacy-y" or "explicit-y" or "support-nonfinite-y" => control with { Position = control.Position with { Y = float.NaN } },
            "support-y" => control with { Position = control.Position with { Y = 1 } },
            "negative-zero" => control with { Position = control.Position with { Y = BitConverter.UInt32BitsToSingle(0x80000000) } },
            "support-binding" => control with { SupportBinding = null },
            "height" => control with { HeightMode = null },
            "enum" => control with { HeightMode = (MapMarkerHeightMode)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var findings = new List<MapDocumentFinding>();
        Assert.Single(MapMarkerValidation.Validate(invalid, findings));
        MapDocumentFinding finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field, finding.FieldPath);
        Assert.Equal(invalid.Id, finding.MarkerId);
        Assert.Equal(MapMarkerValidation.Validate(invalid), findings.Select(f => f.Message));
        if (reason == MapDocumentRefusal.ValueNonfinite) Assert.Null(finding.ActualNumber);
        if (reason == MapDocumentRefusal.MarkerHeightChoiceInvalid) Assert.Equal((double)invalid.Position.Y, finding.ActualNumber);
        if (variant == "version")
        {
            Assert.Equal(3, finding.ActualVersion);
            Assert.Equal(new[] { 1, 2 }, finding.SupportedVersions);
        }
        if (variant == "enum")
        {
            Assert.Equal(typeof(MapMarkerHeightMode), finding.EnumType);
            Assert.Equal(99L, finding.ActualInteger);
        }
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerValidation.Require(invalid));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        var aggregate = Assert.Single(error.Findings!);
        Assert.Equal(finding.Reason, aggregate.Reason);
        Assert.Equal(finding.MarkerId, aggregate.MarkerId);
        Assert.Equal(finding.FieldPath, aggregate.FieldPath);
        Assert.Equal(finding.SupportedVersions, aggregate.SupportedVersions);
        Assert.Empty(MapMarkerValidation.Validate(control));
        MapMarkerValidation.Require(control);
    }
    [Theory]
    [InlineData("foreign-member", MapDocumentRefusal.AuthoredOwnershipMismatch, "$.supportBinding.searchAbove")]
    [InlineData("surface-id", MapDocumentRefusal.SchemaValueRequired, "$.supportBinding.surfaceId")]
    [InlineData("space-id", MapDocumentRefusal.SchemaValueRequired, "$.supportBinding.spaceId")]
    [InlineData("reference-y", MapDocumentRefusal.ValueNonfinite, "$.supportBinding.referenceY")]
    [InlineData("search-above", MapDocumentRefusal.ValueOutsideRange, "$.supportBinding.searchAbove")]
    public void SupportBindingFindings_KeepTheTypedAggregate(string invalid, MapDocumentRefusal reason, string path)
    {
        var control = MapMarkerHeights.Apply(MarkerFixtures.BasicMarker(),
            invalid is "foreign-member" or "surface-id" ? MarkerFixtures.SurfaceHeight() : MarkerFixtures.SpaceHeight());
        if (invalid == "search-above") control = control with { SupportBinding = control.SupportBinding! with { SearchAbove = 0 } };
        var binding = control.SupportBinding!;
        var broken = invalid switch
        {
            "foreign-member" => binding with { SearchAbove = 0 },
            "surface-id" => binding with { SurfaceId = " " },
            "space-id" => binding with { SpaceId = " " },
            "reference-y" => binding with { ReferenceY = float.NaN },
            _ => binding with { SearchAbove = -1 },
        };
        var marker = control with { SupportBinding = broken };
        var findings = new List<MapDocumentFinding>();
        Assert.Single(MapMarkerValidation.Validate(marker, findings));
        var finding = Assert.Single(findings);
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerValidation.Require(marker));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(control.Id, error.MarkerId);
        Assert.Equal(path, error.FieldPath);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(findings, error.Findings);
        Assert.Same(binding, control.SupportBinding);
        Assert.Empty(MapMarkerValidation.Validate(control));
        MapMarkerValidation.Require(control);
    }
}
