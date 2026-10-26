using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFixtureDirectoryTests
{
    [Fact]
    public void Cleanup_removes_readonly_git_objects()
    {
        string root = NewDirectory();
        string objects = Path.Combine(root, ".git", "objects", "ab");
        Directory.CreateDirectory(objects);
        string path = Path.Combine(objects, "object");
        File.WriteAllText(path, "immutable object");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        File.SetAttributes(objects, File.GetAttributes(objects) | FileAttributes.ReadOnly);
        try
        {
            RendererPerfFixtureDirectory.Delete(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(objects)) File.SetAttributes(objects, File.GetAttributes(objects) & ~FileAttributes.ReadOnly);
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_unlinks_directory_aliases_without_touching_their_targets(bool dangling)
    {
        string root = NewDirectory(), target = NewDirectory();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "keep.txt"), link = Path.Combine(root, "linked");
        File.WriteAllText(sentinel, "outside fixture");
        File.SetAttributes(sentinel, FileAttributes.ReadOnly);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            if (dangling)
            {
                File.SetAttributes(sentinel, FileAttributes.Normal);
                Directory.Delete(target, true);
            }
            RendererPerfFixtureDirectory.Delete(root);
            Assert.False(Directory.Exists(root));
            if (!dangling)
            {
                Assert.Equal("outside fixture", File.ReadAllText(sentinel));
                Assert.True((File.GetAttributes(sentinel) & FileAttributes.ReadOnly) != 0);
            }
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(sentinel)) File.SetAttributes(sentinel, FileAttributes.Normal);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    static string NewDirectory() => Path.Combine(Path.GetTempPath(), "khaozengine-renderer-cleanup-" + Guid.NewGuid().ToString("N"));
}
