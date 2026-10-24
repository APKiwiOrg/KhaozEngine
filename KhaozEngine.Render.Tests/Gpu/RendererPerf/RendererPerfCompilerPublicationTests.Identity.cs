using System;
using System.IO;
using System.Linq;
using Xunit;
using Microsoft.Win32.SafeHandles;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData("root", false)]
    [InlineData("inventory", false)]
    [InlineData("producer", false)]
    [InlineData("root", true)]
    [InlineData("inventory", true)]
    public void Replaced_opened_or_earlier_artifact_refuses_same_length_bytes(string target, bool earlier)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        var writer = RendererPerfPublicationWriter.Files(f.Directory);
        Assert.Empty(f.Publish(writer).Failures);
        string name = f.Report.Manifest.UnitId + ".json";
        string relative = target == "root" ? name : target == "inventory" ? f.Report.Manifest.UnitId + "-compiler-inventory.json" : "work-models.json";
        string path = Path.Combine(f.Directory, relative);
        bool replaced = false;
        int opened = -1;
        string[] paths = new[] { name }.Concat(writer.ExpectedSidecars.Select(e => e.Path)).ToArray();
        File.Copy(path, path + ".held-copy");
        var budget = f.Budget with
        {
            Boundary = stage =>
        {
            if (stage == "renderer.verify.file.open.after") opened++;
            if (replaced || !(earlier ? stage == "renderer.verify.complete" : stage == "renderer.verify.file.open.after" && paths[opened] == relative)) return;
            byte[] bytes = File.ReadAllBytes(path + ".held-copy");
            bytes[0] = (byte)'!';
            string replacement = path + ".replacement";
            File.WriteAllBytes(replacement, bytes);
            File.Move(replacement, path, true);
            replaced = true;
        }
        };
        var error = Record.Exception(() => RendererPerfArtifactVerification.Verify(f.Directory, name, budget));
        Assert.True(replaced);
        Assert.NotNull(error);
        Assert.Equal(new FileInfo(path + ".held-copy").Length, new FileInfo(path).Length);
        Assert.NotEqual(0, RunConsole(f.Directory, name).Exit);
        _output.WriteLine("identity target={0} earlier={1} heldChecks={2} refusal={3}", target, earlier, opened, error);
    }
    [Fact]
    public void Unavailable_platform_identity_refuses_without_fallback()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        Assert.Empty(f.Publish(RendererPerfPublicationWriter.Files(f.Directory)).Failures);
        var unavailable = new PlatformNotSupportedException("actual identity facility unavailable");
        Assert.Same(unavailable, Assert.Throws<PlatformNotSupportedException>(() => RendererPerfArtifactVerification.Verify(
            f.Directory, f.Report.Manifest.UnitId + ".json", identity: new UnavailableIdentity(unavailable))));
    }
    sealed class UnavailableIdentity(Exception error) : IRendererPerfArtifactIdentity
    {
        public SafeFileHandle Open(SafeFileHandle? parent, string path, bool directory)
            => RendererPerfArtifactIdentity.Platform.Open(parent, path, directory);
        public RendererPerfFileStatus Handle(SafeFileHandle handle) => throw error;
        public RendererPerfFileStatus Entry(SafeFileHandle? parent, string path) => throw new InvalidOperationException("No fallback after identity refusal.");
    }
    [Fact]
    public void Replaced_evidence_root_refuses_even_when_all_leased_files_remain_valid()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        var writer = RendererPerfPublicationWriter.Files(f.Directory);
        Assert.Empty(f.Publish(writer).Failures);
        string held = f.Directory + ".held-root";
        bool replaced = false;
        try
        {
            var budget = f.Budget with
            {
                Boundary = stage =>
            {
                if (replaced || stage != "renderer.verify.complete") return;
                Directory.Move(f.Directory, held);
                Directory.CreateDirectory(f.Directory);
                replaced = true;
            }
            };
            Assert.Throws<IOException>(() => RendererPerfArtifactVerification.Verify(f.Directory, f.Report.Manifest.UnitId + ".json", budget));
            Assert.True(replaced);
            Assert.True(File.Exists(Path.Combine(held, f.Report.Manifest.UnitId + ".json")));
        }
        finally { if (Directory.Exists(held)) Directory.Delete(held, true); }
    }

}
