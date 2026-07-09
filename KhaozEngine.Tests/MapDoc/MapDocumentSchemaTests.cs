using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The embedded map document schema accepts what SaveText produces and rejects structural garbage.</summary>
    public class MapDocumentSchemaTests
    {
        [Fact]
        public void SchemaJson_IsAvailable()
        {
            string schema = MapDocumentSchema.GetJson();
            Assert.Contains("\"$id\"", schema);
            Assert.Contains("formatVersion", schema);
        }

        [Fact]
        public void SavedDocument_PassesSchema()
        {
            string json = MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc());
            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void MissingRequiredSections_FailSchema()
        {
            ValidationReport report = JsonSchemaValidator.Validate("{\"id\":\"x\"}", MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }

        [Fact]
        public void WrongTypes_FailSchema()
        {
            string json = MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc())
                .Replace("\"formatVersion\": 1", "\"formatVersion\": \"one\"");
            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.False(report.IsValid);
        }
    }
}
