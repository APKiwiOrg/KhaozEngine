using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void Unavailable_exclusion_refuses_before_read_and_closes_acquired_handles(bool missingEntryPoint, int failAt)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        Assert.Empty(f.Publish(RendererPerfPublicationWriter.Files(f.Directory)).Failures);
        var identity = new UnavailableExclusion(missingEntryPoint, failAt);
        int reads = 0, acquired = 0;
        var budget = f.Budget with
        {
            Boundary = stage =>
            {
                if (stage == "renderer.verify.chunk.before") reads++;
                if (stage == "renderer.verify.file.open.after") acquired++;
            }
        };
        var error = Record.Exception(() => RendererPerfArtifactVerification.Verify(f.Directory, f.Report.Manifest.UnitId + ".json", budget, identity));
        Assert.True(identity.Attempted);
        Assert.Same(identity.Error, error);
        Assert.Equal(failAt - 1, acquired);
        if (failAt == 1) Assert.Equal(0, reads); else Assert.True(reads > 0);
        Assert.All(identity.Handles, h => Assert.True(h.IsClosed));
        foreach (string path in identity.Files)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.WriteByte(bytes[0]); writer.Flush(true); }
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        Assert.Equal(0, RunConsole(f.Directory, f.Report.Manifest.UnitId + ".json").Exit);
        _output.WriteLine("exclusion unavailable missingEntryPoint={0} failAt={1} attempted=true reads={2} acquired={3} closed={4} released=true error={5}", missingEntryPoint, failAt, reads, acquired, identity.Handles.Count, error);
    }
    sealed class UnavailableExclusion(bool missingEntryPoint, int failAt) : IRendererPerfArtifactIdentity
    {
        internal readonly List<SafeFileHandle> Handles = [];
        internal readonly List<string> Files = [];
        int _attempts;
        internal Exception? Error;
        internal bool Attempted;
        public SafeFileHandle Open(SafeFileHandle? parent, string path, bool directory)
        {
            var handle = RendererPerfArtifactIdentity.Platform.Open(parent, path, directory);
            Handles.Add(handle);
            if (!directory) Files.Add(path);
            return handle;
        }
        public RendererPerfFileStatus Handle(SafeFileHandle handle) => RendererPerfArtifactIdentity.Platform.Handle(handle);
        public RendererPerfFileStatus Entry(SafeFileHandle? parent, string path) => RendererPerfArtifactIdentity.Platform.Entry(parent, path);
        public void ExcludeWriters(SafeFileHandle handle)
        {
            if (++_attempts != failAt) { RendererPerfArtifactIdentity.Platform.ExcludeWriters(handle); return; }
            Attempted = true;
            Assert.False(RendererPerfArtifactIdentity.Platform.Handle(handle).Directory);
            // Inject only the unavailable platform operation. Acquisition, identity and disposal
            // remain the real FileSet/DirectoryReader owners, rather than fabricated failures.
            Error = missingEntryPoint
                ? new PlatformNotSupportedException("Injected exclusion facility missing.", new EntryPointNotFoundException("flock"))
                : new IOException("Injected exclusion facility unavailable.", new System.ComponentModel.Win32Exception(OperatingSystem.IsWindows() ? 50 : OperatingSystem.IsMacOS() ? 45 : 95));
            throw Error;
        }
    }
    [Theory]
    [InlineData("renderer.verify.file.open.after", false)]
    [InlineData("renderer.verify.complete", false)]
    [InlineData("renderer.verify.complete", true)]
    public void Exclusion_is_released_after_late_acquisition_or_budget_refusal(string boundary, bool cancel)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        Assert.Empty(f.Publish(RendererPerfPublicationWriter.Files(f.Directory)).Failures);
        var released = new IOException("held reader refusal");
        string path = Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".json");
        byte[] before = File.ReadAllBytes(path);
        bool reached = false;
        var budget = f.Budget with
        {
            Boundary = stage =>
            {
                if (stage != boundary || reached) return;
                reached = true;
                var error = Record.Exception(() => { using var writer = OpenWriter(); writer.WriteByte((byte)'!'); writer.Flush(true); });
                AssertSharingContention(error);
                if (cancel) f.Cancellation.Cancel(); else throw released;
            }
        };
        var error = Record.Exception(() => RendererPerfArtifactVerification.Verify(f.Directory, f.Report.Manifest.UnitId + ".json", budget));
        Assert.True(reached);
        if (cancel) Assert.Equal(f.Cancellation.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
        else Assert.Same(released, error);
        Assert.Equal(before, File.ReadAllBytes(path));
        using (var writer = OpenWriter()) { writer.WriteByte(before[0]); writer.Flush(true); }
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(0, RunConsole(f.Directory, f.Report.Manifest.UnitId + ".json").Exit);
        _output.WriteLine("exclusion release boundary={0} reached=true cancel={1} unchanged=true released=true error={2}", boundary, cancel, error);
        FileStream OpenWriter() => new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }
}
