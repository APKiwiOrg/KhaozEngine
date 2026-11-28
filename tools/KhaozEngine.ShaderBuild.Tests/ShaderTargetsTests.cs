using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Loader;
using System.Xml.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class ShaderTargetsTests : IClassFixture<ShaderTargetsTests.ShaderOwnerProject>
{
    private readonly ShaderOwnerProject _project;

    public ShaderTargetsTests(ShaderOwnerProject project) => _project = project;

    [Fact]
    public void A_project_with_a_manifest_embeds_the_bundle_and_packs_no_tool_dependency()
    {
        Assert.True(_project.FirstBuild.Exit == 0, _project.FirstBuild.Output);

        var context = new AssemblyLoadContext("shader-owner-fixture", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(_project.Assembly)));
            var bundle = GpuShaderBundle.FromAssembly(assembly);
            Assert.Equal(SlangcPins.Load(Path.Combine(AppContext.BaseDirectory, "slangc.pins.json")).Version, bundle.SlangVersion);
            Assert.Equal("vertexMain", bundle.Get("sprite").Vertex.EntryPoint);
        }
        finally
        {
            context.Unload();
        }

        var packages = Path.Combine(_project.Folder, "packages");
        var pack = ShaderOwnerProject.Dotnet(_project.Folder, "pack", _project.Csproj, "-c", "Release", "--no-build", "-o", packages);
        Assert.True(pack.Exit == 0, pack.Output);
        using var package = ZipFile.OpenRead(Directory.GetFiles(packages, "*.nupkg").Single(path => !path.EndsWith(".symbols.nupkg", StringComparison.Ordinal)));
        using var nuspec = package.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
        var dependencies = XDocument.Load(nuspec).Descendants().Where(element => element.Name.LocalName == "dependency")
            .Select(element => (string?)element.Attribute("id"));
        Assert.DoesNotContain("KhaozEngine.ShaderBuild", dependencies);
    }

    [Fact]
    public void The_bundle_rebuilds_only_when_a_shader_input_changes()
    {
        Assert.True(_project.FirstBuild.Exit == 0, _project.FirstBuild.Output);
        Assert.True(File.Exists(_project.Bundle), _project.Bundle);
        var built = File.GetLastWriteTimeUtc(_project.Bundle);

        var unchanged = _project.Build();
        Assert.True(unchanged.Exit == 0, unchanged.Output);
        Assert.Equal(built, File.GetLastWriteTimeUtc(_project.Bundle));

        File.SetLastWriteTimeUtc(_project.ShaderSource, built.AddSeconds(5));
        var edited = _project.Build();
        Assert.True(edited.Exit == 0, edited.Output);
        Assert.NotEqual(built, File.GetLastWriteTimeUtc(_project.Bundle));
    }

    /// <summary>A packable library inside the repository tree, so the repository's Directory.Build files apply to it.</summary>
    public sealed class ShaderOwnerProject : IDisposable
    {
        private const string Name = "ShaderOwnerFixture";

        public ShaderOwnerProject()
        {
            Folder = Path.Combine(AppContext.BaseDirectory, "shader-targets-fixture", Guid.NewGuid().ToString("N"));
            var shaders = Path.Combine(Folder, "Shaders");
            Directory.CreateDirectory(shaders);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sprite.slang"), ShaderSource);
            File.WriteAllText(
                Path.Combine(shaders, "programs.json"),
                """{ "programs": [ { "id": "sprite", "source": "sprite.slang", "vertex": "vertexMain", "fragment": "fragmentMain" } ] }""");
            File.WriteAllText(Path.Combine(Folder, "Marker.cs"), $"namespace {Name};\n\npublic static class Marker\n{{\n}}\n");
            File.WriteAllText(
                Csproj,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <AssemblyName>{Name}</AssemblyName>
                    <PackageId>{Name}</PackageId>
                    <Version>1.0.0</Version>
                    <KhaozShaderManifest>Shaders/programs.json</KhaozShaderManifest>
                  </PropertyGroup>
                </Project>
                """);
            FirstBuild = Build();
        }

        public string Folder { get; }

        public string Csproj => Path.Combine(Folder, Name + ".csproj");

        public string ShaderSource => Path.Combine(Folder, "Shaders", "sprite.slang");

        public string Assembly => Path.Combine(Folder, "bin", "Release", "net10.0", Name + ".dll");

        public string Bundle => Path.Combine(Folder, "obj", "Release", "net10.0", "khaoz.shaders.json");

        public (int Exit, string Output) FirstBuild { get; }

        public (int Exit, string Output) Build() => Dotnet(Folder, "build", Csproj, "-c", "Release");

        public static (int Exit, string Output) Dotnet(string workingDirectory, params string[] args)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            start.ArgumentList.Add("--disable-build-servers");
            // The test host inherits these from the outer dotnet test, and they would pin the nested build to its MSBuild.
            foreach (var variable in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly" })
                start.Environment.Remove(variable);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                process.Kill(entireProcessTree: true);
                return (-1, $"dotnet {string.Join(' ', args)} did not finish within 10 minutes.");
            }
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }
    }
}
