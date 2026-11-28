using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class BundleBuildTests : IDisposable
{
    private static readonly Lazy<string> PinnedSlangc = new(() => SlangcResolver.Resolve(
        SlangcPins.Load(Path.Combine(AppContext.BaseDirectory, "slangc.pins.json")),
        Environment.GetEnvironmentVariable(SlangcResolver.OverrideVariable),
        SlangcResolver.DefaultCacheRoot()));

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "khaozengine-bundle-build-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Builds_the_sprite_fixture_into_a_bundle_the_runtime_reader_accepts()
    {
        var (exit, error, bundleBytes) = Build(CopyFixture("lf", crlf: false));

        Assert.True(exit == 0, error);
        using var stream = new MemoryStream(bundleBytes);
        var bundle = GpuShaderBundle.Load(stream, "sprite-test");
        var sprite = bundle.Get("sprite");
        Assert.Equal("2026.23", bundle.SlangVersion);
        Assert.Equal(
            new[]
            {
                new GpuShaderBinding("Tex", 0, 0, GpuResourceKind.TextureReadOnly, 0, 0),
                new GpuShaderBinding("Samp", 0, 1, GpuResourceKind.Sampler, 0, 0),
                new GpuShaderBinding("Vp", 1, 0, GpuResourceKind.UniformBuffer, 0, 0),
            },
            sprite.Bindings);
        foreach (var (stage, entryPoint) in new[] { (sprite.Vertex, "vertexMain"), (sprite.Fragment, "fragmentMain") })
        {
            Assert.Equal(entryPoint, stage.EntryPoint);
            Assert.False(stage.Spirv.IsEmpty);
            Assert.Contains(entryPoint + "(", stage.Msl);
            Assert.Contains(entryPoint + "(", stage.Hlsl);
        }
    }

    [Fact]
    public void A_crlf_checkout_builds_the_same_bundle_bytes()
    {
        var lf = Build(CopyFixture("lf", crlf: false));
        var crlf = Build(CopyFixture("crlf", crlf: true));

        Assert.True(lf.Exit == 0 && crlf.Exit == 0, lf.Error + crlf.Error);
        Assert.Equal(lf.Bundle, crlf.Bundle);
    }

    [Fact]
    public void A_compile_error_exits_1_with_the_file_and_line()
    {
        var source = Path.Combine(_folder, "broken", "broken.slang");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(
            source,
            "[shader(\"vertex\")] float4 vertexMain() : SV_Position { return float4(0.0); }\n"
            + "[shader(\"fragment\")] float4 fragmentMain() : SV_Target0 { return undefinedThing; }\n");
        File.WriteAllText(
            Path.Combine(_folder, "broken", "programs.json"),
            """{ "programs": [ { "id": "broken", "source": "broken.slang", "vertex": "vertexMain", "fragment": "fragmentMain" } ] }""");

        var (exit, error, _) = Build(Path.Combine(_folder, "broken"));

        Assert.Equal(1, exit);
        Assert.Contains(source + "(2,", error);
    }

    private string CopyFixture(string name, bool crlf)
    {
        var folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        foreach (var file in new[] { "sprite.slang", "line-endings.slang", "programs.json" })
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)).Replace("\r\n", "\n");
            File.WriteAllText(Path.Combine(folder, file), crlf ? text.Replace("\n", "\r\n") : text);
        }
        return folder;
    }

    private (int Exit, string Error, byte[] Bundle) Build(string manifestFolder)
    {
        var output = Path.Combine(manifestFolder, "out", "bundle.json");
        var error = new StringWriter();
        var exit = Program.Run(
            [
                "--manifest", Path.Combine(manifestFolder, "programs.json"),
                "--out", output,
                "--pins", Path.Combine(AppContext.BaseDirectory, "slangc.pins.json"),
                "--slangc", PinnedSlangc.Value,
            ],
            TextWriter.Null,
            error);
        return (exit, error.ToString(), File.Exists(output) ? File.ReadAllBytes(output) : []);
    }
}
