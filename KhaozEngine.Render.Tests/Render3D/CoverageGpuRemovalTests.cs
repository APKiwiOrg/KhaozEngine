using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageGpuRemovalTests
{
    [Fact]
    public void Retired_display_and_constructor_APIs_are_absent()
    {
        var assembly = typeof(Scene3D).Assembly;
        foreach (string name in new[] { "CoverageEvaluator", "CoverageSurfaceNormals", "CoverageCertificate", "CoverageRenderStamp", "CoverageRenderPass", "CoverageRenderContext" })
            Assert.Null(assembly.GetType("KhaozEngine.Render3D." + name));
        Assert.DoesNotContain(typeof(CoverageCertification).GetMethods(), m => m.Name == "Validate");
        Assert.Null(typeof(Scene3D).GetProperty("CoveredHostPreparationCount"));
        foreach (string name in new[] { "Posed", "Before", "Candidate", "Passes", "PassRoles", "Certificate" })
            Assert.Null(typeof(CoveragePreparation).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        string root = Root();
        foreach (string path in Directory.GetFiles(Path.Combine(root, "KhaozEngine.Render3D"), "Scene3D*.cs"))
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("CoverageReferenceConstructor.TryConstruct", source);
            Assert.DoesNotContain("CoverageCertification.Validate", source);
        }
        // The opt-in diagnostic reference is an asynchronous readback consumer, outside draw construction.
        string diagnostic = File.ReadAllText(Path.Combine(root, "KhaozEngine.Render3D", "Coverage", "CoverageVerificationReadbacks.cs"));
        Assert.Contains("CoverageReferenceConstructor.TryConstruct", diagnostic);
    }

    [Fact]
    public void Frozen_captures_remain_immutable_oracle_evidence()
    {
        string root = Root();
        string fixtures = Path.Combine(root, "KhaozEngine.Render.Tests", "Fixtures");
        var paths = Directory.GetFiles(Path.Combine(fixtures, "coverage-exact-v1"), "*.json")
            .Append(Path.Combine(fixtures, "coverage-reference-v1", "outputs.sha256"))
            .OrderBy(p => Path.GetRelativePath(root, p), StringComparer.Ordinal).ToArray();
        string rows = string.Concat(paths.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + " "
            + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))) + "\n"));
        Assert.Equal("5b19e2bf21bb08d5e39d23614459abf527f0be9498de6bc83c87cf45282af393", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rows))));
        Assert.Equal(144, File.ReadLines(paths.Single(p => p.EndsWith("outputs.sha256", StringComparison.Ordinal))).Count());
        Assert.Equal(72, CoverageExactCaptureTests.ReadManifest().Cases.Length);
        int passes = 0, negatives = 0;
        foreach (var spec in CoverageReferenceCases.Specs)
        {
            var c = CoverageReferenceCases.Get(spec.Id);
            var oracle = new CoverageOracle(c.Topology, c.InverseBind);
            if (oracle.TryCheck(c.Palette, c.Model, c.Commands, out _, out var failure)) passes++;
            else
            {
                Assert.Equal(CoverageOracleStage.Certification, failure.Stage);
                Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Failure.Code);
                negatives++;
            }
        }
        Assert.Equal(66, passes);
        Assert.Equal(6, negatives);
    }

    static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
