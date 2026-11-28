using System.IO;
using System.Text;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class GpuShaderBundleTests
    {
        const string ValidBundleJson = """
            {
              "format": "khaoz-shader-bundle",
              "version": 1,
              "slangc": "2026.23",
              "programs": [
                {
                  "id": "sprite",
                  "variant": "",
                  "vertexPreciseMath": false,
                  "bindings": [
                    { "name": "Tex", "set": 0, "binding": 0, "kind": "TextureReadOnly", "metal": 0, "hlsl": 0 },
                    { "name": "Samp", "set": 0, "binding": 1, "kind": "Sampler", "metal": 0, "hlsl": 0 },
                    { "name": "Vp", "set": 1, "binding": 0, "kind": "UniformBuffer", "metal": 0, "hlsl": 0 }
                  ],
                  "vertex": { "entryPoint": "vertexMain", "spirv": "AwIjBw==", "msl": "vertex msl", "hlsl": "vertex hlsl" },
                  "fragment": { "entryPoint": "fragmentMain", "spirv": "AwIjBw==", "msl": "fragment msl", "hlsl": "fragment hlsl" }
                }
              ]
            }
            """;

        static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

        [Fact]
        public void Loads_a_program_and_its_bindings_by_id()
        {
            var bundle = GpuShaderBundle.Load(Utf8(ValidBundleJson), "test");
            var program = bundle.Get("sprite");
            Assert.Equal("2026.23", bundle.SlangVersion);
            Assert.Equal("vertexMain", program.Vertex.EntryPoint);
            Assert.Equal(new byte[] { 0x03, 0x02, 0x23, 0x07 }, program.Fragment.Spirv.ToArray());
            Assert.Equal(new GpuShaderBinding("Tex", 0, 0, GpuResourceKind.TextureReadOnly, 0, 0), program.Bindings[0]);
            Assert.Equal(new GpuShaderBinding("Vp", 1, 0, GpuResourceKind.UniformBuffer, 0, 0), program.Bindings[2]);
        }

        [Theory]
        [InlineData("\"version\": 1", "\"version\": 2", 2)]
        [InlineData("\"format\": \"khaoz-shader-bundle\"", "\"format\": \"other-bundle\"", 1)]
        public void Refuses_another_format_or_version(string field, string replacement, int foundVersion)
        {
            var ex = Assert.Throws<ShaderValidationException>(
                () => GpuShaderBundle.Load(Utf8(ValidBundleJson.Replace(field, replacement)), "test"));
            Assert.Equal(ShaderValidationRefusal.BundleFormatUnsupported, ex.Reason);
            Assert.Equal("test", ex.Label);
            Assert.Equal(foundVersion, ex.FormatVersion);
        }

        [Theory]
        [InlineData("missing", "", ShaderValidationRefusal.BundleProgramUnknown)]
        [InlineData("sprite", "hex", ShaderValidationRefusal.BundleVariantUnknown)]
        public void Refuses_an_unknown_program_or_variant(string id, string variant, ShaderValidationRefusal reason)
        {
            var bundle = GpuShaderBundle.Load(Utf8(ValidBundleJson), "test");
            var ex = Assert.Throws<ShaderValidationException>(() => bundle.Get(id, variant));
            Assert.Equal(reason, ex.Reason);
            Assert.Equal(id, ex.Label);
        }

        [Theory]
        [InlineData("truncated")]
        [InlineData("kind spelled in another case")]
        [InlineData("spirv not base64")]
        [InlineData("program repeated")]
        public void Refuses_a_malformed_bundle_as_corrupt(string defect)
        {
            var json = defect switch
            {
                "truncated" => ValidBundleJson[..(ValidBundleJson.Length / 2)],
                "kind spelled in another case" => ValidBundleJson.Replace("\"Sampler\"", "\"sampler\""),
                "spirv not base64" => ValidBundleJson.Replace("\"AwIjBw==\"", "\"not base64!\""),
                _ => ValidBundleJson.Replace("\"programs\": [", "\"programs\": [" + ProgramObject() + ","),
            };
            var ex = Assert.Throws<ShaderValidationException>(() => GpuShaderBundle.Load(Utf8(json), "test"));
            Assert.Equal(ShaderValidationRefusal.BundleCorrupt, ex.Reason);
            Assert.Equal("test", ex.Label);
        }

        [Fact]
        public void Refuses_an_assembly_without_an_embedded_bundle_by_resource_name()
        {
            var assembly = typeof(GpuShaderBundleTests).Assembly;
            var ex = Assert.Throws<ShaderValidationException>(() => GpuShaderBundle.FromAssembly(assembly));
            Assert.Equal(ShaderValidationRefusal.BundleMissing, ex.Reason);
            Assert.Equal(assembly.GetName().Name + ".khaoz.shaders.json", ex.Label);
        }

        static string ProgramObject()
        {
            var start = ValidBundleJson.IndexOf("{\n      \"id\"", System.StringComparison.Ordinal);
            var end = ValidBundleJson.LastIndexOf("\n  ]", System.StringComparison.Ordinal);
            return ValidBundleJson[start..end];
        }
    }
}
