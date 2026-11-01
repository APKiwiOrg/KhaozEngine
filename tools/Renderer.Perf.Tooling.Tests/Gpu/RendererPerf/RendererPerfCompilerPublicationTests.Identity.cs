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
        bool reached = false, replaced = false;
        Exception? mutationError = null;
        var originalId = RendererPerfArtifactIdentity.Platform.Entry(null, path);
        int opened = -1;
        string[] paths = new[] { name }.Concat(writer.ExpectedSidecars.Select(e => e.Path)).ToArray();
        File.Copy(path, path + ".held-copy");
        var budget = f.Budget with
        {
            Boundary = stage =>
        {
            if (stage == "renderer.verify.file.open.after") opened++;
            if (reached || !(earlier ? stage == "renderer.verify.complete" : stage == "renderer.verify.file.open.after" && paths[opened] == relative)) return;
            reached = true;
            byte[] bytes = File.ReadAllBytes(path + ".held-copy");
            bytes[0] = (byte)'!';
            string replacement = path + ".replacement";
            File.WriteAllBytes(replacement, bytes);
            mutationError = Record.Exception(() => File.Move(replacement, path, true));
            replaced = mutationError is null;
        }
        };
        var error = Record.Exception(() => RendererPerfArtifactVerification.Verify(f.Directory, name, budget));
        Assert.True(reached);
        Assert.Equal(new FileInfo(path + ".held-copy").Length, new FileInfo(path).Length);
        if (replaced)
        {
            Assert.IsType<IOException>(error);
            Assert.NotEqual(originalId, RendererPerfArtifactIdentity.Platform.Entry(null, path));
            Assert.NotEqual(0, RunConsole(f.Directory, name).Exit);
        }
        else
        {
            AssertBlockedReplacement(mutationError);
            Assert.Null(error);
            Assert.Equal(originalId, RendererPerfArtifactIdentity.Platform.Entry(null, path));
            Assert.Equal(File.ReadAllBytes(path + ".held-copy"), File.ReadAllBytes(path));
            Assert.Equal(0, RunConsole(f.Directory, name).Exit);
        }
        using (var released = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) released.Flush(true);
        _output.WriteLine("identity target={0} earlier={1} reached=true replaced={2} heldChecks={3} mutationError={4} refusal={5} released=true", target, earlier, replaced, opened, mutationError, error);
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
        public void ExcludeWriters(SafeFileHandle handle) => RendererPerfArtifactIdentity.Platform.ExcludeWriters(handle);
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
        bool reached = false, replaced = false;
        Exception? mutationError = null;
        string rootFile = Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".json");
        byte[] original = File.ReadAllBytes(rootFile);
        var originalId = RendererPerfArtifactIdentity.Platform.Entry(null, rootFile);
        try
        {
            var budget = f.Budget with
            {
                Boundary = stage =>
            {
                if (reached || stage != "renderer.verify.complete") return;
                reached = true;
                mutationError = Record.Exception(() => Directory.Move(f.Directory, held));
                if (mutationError is not null) return;
                Directory.CreateDirectory(f.Directory);
                replaced = true;
            }
            };
            var error = Record.Exception(() => RendererPerfArtifactVerification.Verify(f.Directory, f.Report.Manifest.UnitId + ".json", budget));
            Assert.True(reached);
            if (replaced)
            {
                Assert.IsType<IOException>(error);
                rootFile = Path.Combine(held, f.Report.Manifest.UnitId + ".json");
            }
            else { AssertBlockedReplacement(mutationError); Assert.Null(error); }
            Assert.Equal(originalId, RendererPerfArtifactIdentity.Platform.Entry(null, rootFile));
            Assert.Equal(original, File.ReadAllBytes(rootFile));
            using (var released = new FileStream(rootFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) released.Flush(true);
            _output.WriteLine("root replacement reached=true replaced={0} mutationError={1} readerError={2} unchanged=true released=true", replaced, mutationError, error);
        }
        finally { if (Directory.Exists(held)) Directory.Delete(held, true); }
    }

    static void AssertBlockedReplacement(Exception? error)
    {
        Assert.True(OperatingSystem.IsWindows(), "Only Windows blocked replacement is an admitted alternative.");
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Contains(error.HResult & 0xffff, new[] { 5, 32 });
    }
}
