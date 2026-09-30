using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Validation.ContentValidationFixtures;

namespace KhaozEngine.Tests.Catalog.Validation;

public class ContentKeyShapeTests
{
    public static IEnumerable<object?[]> Cases()
    {
        yield return ["a", null, null];
        yield return ["a0_b9", null, null];
        yield return [new string('a', 64), null, null];
        yield return [string.Empty, "empty", "empty"];
        yield return [new string('a', 65), "65 characters long", "65 characters long"];
        yield return ["1a", "a leading digit", "a leading digit"];
        yield return ["_a", "a leading underscore", "a leading underscore"];
        yield return ["a_", "a trailing underscore", "a trailing underscore"];
        yield return ["a__b", "a double underscore at position 2", "a double underscore at position 2"];
        yield return ["9__", "a double underscore at position 2", "a double underscore at position 2"];
        yield return ["A", "outside the character set at position 0", "outside the character set at position 0"];
        yield return ["a-b", "outside the character set at position 1", "outside the character set at position 1"];
        yield return ["a b", "outside the character set at position 1", "outside the character set at position 1"];
        yield return ["a\u00e9", "outside the character set at position 1", "outside the character set at position 1"];
        yield return ["a" + new string('\u00e9', 32), "outside the character set at position 1", "65 characters long"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AuthoringAndValidatorEnforceTheSameShapeWithTheirExistingDiagnosticUnits(
        string key,
        string? stringDefect,
        string? byteDefect)
    {
        Assert.Equal(stringDefect, ContentKeyShape.Defect(key));
        Assert.Equal(stringDefect, ContentKeyRules.Defect(key));
        ContentTypeRegistry registry = GameRegistry(Schema());
        ContentRow row = GameRow(7, key);
        Assert.Equal(byteDefect, ContentKeyRules.Defect(row.Key.Utf8));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        if (byteDefect is null)
        {
            Assert.True(report.IsValid, Describe(report));
            AssertNone(report, "KEC0001");
        }
        else
        {
            ContentFinding finding = Single(report, "KEC0001");
            Assert.Equal(7, finding.Id);
            Assert.Contains("which is " + byteDefect + ".", finding.Message, StringComparison.Ordinal);
            Assert.Contains(ContentKeyShape.Rule, finding.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MalformedKeysReachTheSweepIntactAndEveryBadRowIsReported()
    {
        ContentTypeRegistry registry = GameRegistry(Schema());
        ContentSnapshot candidate = Snapshot(
            registry,
            GameRow(1, string.Empty),
            GameRow(2, "Upper"),
            GameRow(3, new string('a', 65)));

        ContentValidationReport report = Validate(candidate, registry);

        Assert.Equal(new[] { 1, 2, 3 }, report.Findings.Where(finding => finding.Code == "KEC0001").Select(finding => finding.Id));
    }
}
