using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageArithmeticPortabilityTests
{
    [Fact]
    public void Unrelated_binary_change_and_locationless_loading_preserve_runtime_table_identity()
    {
        using var files = new CoverageBuildFiles("final-fix");
        var input = CoverageBuildInputJson.Read(files.ManifestPath);
        byte[] original = File.ReadAllBytes(typeof(CoverageRefinement).Assembly.Location);
        byte[] changed = original.ToArray();
        // Change only the PE/COFF timestamp. This is an unrelated binary transformation, with identical arithmetic.
        int coff = BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(0x3c)) + 8;
        changed[coff] ^= 1;
        Assert.NotEqual(CoverageBuildInputJson.Hash(original), CoverageBuildInputJson.Hash(changed));
        Assert.Equal(input.Identity, ReadFromMemory(original));
        Assert.Equal(input.Identity, ReadFromMemory(changed));
        var built = CoverageTableBuilder.Build(input);
        Assert.Equal(CoverageBuildInputJson.Hash(original), built.Evidence.Render3DCodeSha256);
        Assert.Equal(CoverageBuildInputJson.Hash(File.ReadAllBytes(typeof(CoverageBuildInputJson).Assembly.Location)), built.Evidence.ToolCodeSha256);
        var table = built.Table;
        Assert.True(new CoverageTableBinding(input.Topology, table).MatchesRepresentatives([0]));

        CoverageRecessionIdentity ReadFromMemory(byte[] renderBytes)
        {
            var context = new AssemblyLoadContext("coverage-locationless", isCollectible: true);
            try
            {
                context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.SingleOrDefault(a => a.GetName().Name == name.Name);
                var render = context.LoadFromStream(new MemoryStream(renderBytes));
                Assert.Empty(render.Location);
                var tool = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(typeof(CoverageBuildInputJson).Assembly.Location)));
                // Only public reader and record properties are invoked. No private-state reflection.
                var reader = tool.GetType(typeof(CoverageBuildInputJson).FullName!, throwOnError: true)!;
                object loaded = reader.GetMethod(nameof(CoverageBuildInputJson.Read))!.Invoke(null, [files.ManifestPath])!;
                object identity = loaded.GetType().GetProperty(nameof(CoverageBuildInput.Identity))!.GetValue(loaded)!;
                string Get(string name) => (string)identity.GetType().GetProperty(name)!.GetValue(identity)!;
                return new(Get(nameof(input.Identity.HostSha256)), Get(nameof(input.Identity.HostMapSha256)),
                    Get(nameof(input.Identity.ConfigurationSha256)), Get(nameof(input.Identity.RefinementSha256)),
                    Get(nameof(input.Identity.ArithmeticSha256)), Get(nameof(input.Identity.StateSchemaSha256)));
            }
            finally { context.Unload(); }
        }
    }
}
