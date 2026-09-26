using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Xunit;

namespace KhaozEngine.Localization.Analyzers.Tests
{
    public class StringIdGeneratorTests
    {
        private const string TargetTypeKey = "build_metadata.AdditionalFiles.KhaozStringIdType";
        private const string AccessibilityKey = "build_metadata.AdditionalFiles.KhaozStringIdAccessibility";

        [Fact]
        public void AnnotatedResx_GeneratesExactStringIdsWithDeterministicMemberNames()
        {
            const string resx = """
                <root>
                  <data name="menu.play"><value>Play</value></data>
                  <data name="Room.Gui2D.Title"><value>2D GUI</value></data>
                  <data name="fly_speed"><value>Fly speed</value></data>
                  <data name="Message.&quot;Path\Root"><value>Quoted path</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/ShowcaseStrings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "KhaozEngine.Showcase.ShowcaseStrings",
                });

            string source = Assert.Single(result.Sources);
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.CompilationErrors);
            Assert.Contains("namespace KhaozEngine.Showcase", source);
            Assert.Contains("internal static class ShowcaseStrings", source);
            Assert.Contains("StringId FlySpeed = new(\"fly_speed\")", source);
            Assert.Contains("StringId MessagePathRoot = new(\"Message.\\\"Path\\\\Root\")", source);
            Assert.Contains("StringId MenuPlay = new(\"menu.play\")", source);
            Assert.Contains("StringId RoomGui2DTitle = new(\"Room.Gui2D.Title\")", source);
            Assert.True(
                source.IndexOf("FlySpeed", System.StringComparison.Ordinal) <
                source.IndexOf("MenuPlay", System.StringComparison.Ordinal));
        }

        [Fact]
        public void ResxWithoutTargetMetadata_GeneratesNothing()
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run("/repo/Strings.resx", resx);

            Assert.Empty(result.Sources);
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.CompilationErrors);
        }

        [Fact]
        public void TypedNonStringData_IsNotGeneratedAsAStringId()
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                  <data name="Icon" type="System.Byte[]"><value>AA==</value></data>
                  <data name="Message" type="System.String, System.Private.CoreLib"><value>Hello</value></data>
                  <data name="Names" type="System.String[]"><value>Alice</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                });

            string source = Assert.Single(result.Sources);
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.CompilationErrors);
            Assert.Contains("StringId MenuPlay", source);
            Assert.Contains("StringId Message", source);
            Assert.DoesNotContain("StringId Icon", source);
            Assert.DoesNotContain("StringId Names", source);
        }

        [Fact]
        public void PublicAccessibility_GeneratesPublicTargetClass()
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                    [AccessibilityKey] = "public",
                });

            string source = Assert.Single(result.Sources);
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.CompilationErrors);
            Assert.Contains("public static class Strings", source);
        }

        [Fact]
        public void MalformedResx_ReportsGeneratorErrorWithoutThrowing()
        {
            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                "<root><data",
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                });

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC005");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("Strings.resx", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
        }

        [Fact]
        public void KeysThatMapToSameMember_ReportCollisionWithoutSource()
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                  <data name="Menu_Play"><value>Play again</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                });

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC006");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("Menu.Play", diagnostic.GetMessage());
            Assert.Contains("Menu_Play", diagnostic.GetMessage());
            Assert.Contains("MenuPlay", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
        }

        [Theory]
        [InlineData("MyGame.123Strings", "internal")]
        [InlineData("MyGame.class", "internal")]
        [InlineData("namespace.Strings", "internal")]
        [InlineData("MyGame.Strings", "protected")]
        public void InvalidTargetConfiguration_ReportsErrorWithoutSource(string targetType, string accessibility)
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = targetType,
                    [AccessibilityKey] = accessibility,
                });

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC004");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("Strings.resx", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
        }

        [Fact]
        public void ExistingSourceType_ReportsTargetCollisionWithoutGeneratedSource()
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                </root>
                """;
            const string source = "namespace MyGame { internal static class Strings { } }";

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                },
                source);

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC007");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("MyGame.Strings", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
        }

        [Fact]
        public void TwoResourcesTargetingSameType_ReportCollisionWithoutGeneratedSource()
        {
            const string firstResx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                </root>
                """;
            const string secondResx = """
                <root>
                  <data name="Menu.Quit"><value>Quit</value></data>
                </root>
                """;
            var metadata = new Dictionary<string, string>
            {
                [TargetTypeKey] = "MyGame.Strings",
            };

            GeneratorHarnessResult result = GeneratorHarness.Run(new[]
            {
                new GeneratorAdditionalFile("/repo/First.resx", firstResx, metadata),
                new GeneratorAdditionalFile("/repo/Second.resx", secondResx, metadata),
            });

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC007");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("MyGame.Strings", diagnostic.GetMessage());
            Assert.Contains("First.resx", diagnostic.GetMessage());
            Assert.Contains("Second.resx", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
        }

        [Fact]
        public void DifferentTargets_WithPathNamesThatSanitizeTheSame_BothGenerate()
        {
            const string resx = """
                <root>
                  <data name="Menu.Play"><value>Play</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(new[]
            {
                new GeneratorAdditionalFile(
                    "/repo/A_B.resx",
                    resx,
                    new Dictionary<string, string> { [TargetTypeKey] = "MyGame.FirstStrings" }),
                new GeneratorAdditionalFile(
                    "/repo/A/B.resx",
                    resx,
                    new Dictionary<string, string> { [TargetTypeKey] = "MyGame.SecondStrings" }),
            });

            Assert.Equal(2, result.Sources.Length);
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.CompilationErrors);
        }

        [Fact]
        public void EmptyResourceKey_ReportsInvalidResxWithoutGeneratedSource()
        {
            const string resx = """
                <root>
                  <data name=""><value>Empty key</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                });

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC005");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("empty resource key", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
        }

        [Fact]
        public void MemberMatchingTargetType_ReportsInvalidResxWithoutGeneratedSource()
        {
            const string resx = """
                <root>
                  <data name="Strings"><value>Text</value></data>
                </root>
                """;

            GeneratorHarnessResult result = GeneratorHarness.Run(
                "/repo/Strings.resx",
                resx,
                new Dictionary<string, string>
                {
                    [TargetTypeKey] = "MyGame.Strings",
                });

            Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "KELOC005");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("Strings", diagnostic.GetMessage());
            Assert.Empty(result.Sources);
            Assert.Empty(result.CompilationErrors);
        }
    }
}
