using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Snapshot;
using StbImageSharp;
using Xunit;

namespace KhaozEngine.Tests.Snapshot
{
    /// <summary>
    /// Headless tests for the runner's encode/write/log/summary plumbing (no GPU): they feed a synthetic RGBA
    /// buffer through <see cref="SnapshotRunner.Save"/> rather than a real capture. The GPU-backed Shot2D/Shot3D
    /// end-to-end path is covered by a gated <c>[GpuFact]</c>.
    /// </summary>
    public class SnapshotRunnerTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "ke-snaprunner-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        // A 2x3 RGBA image with distinct pixels (incl. partial alpha).
        static byte[] SampleRgba() => new byte[]
        {
            255, 0,   0,   255,   0,   255, 0,   255,
            0,   0,   255, 255,   255, 255, 0,   128,
            10,  20,  30,  40,    200, 150, 100, 255,
        };

        [Fact]
        public void Constructor_creates_the_output_directory()
        {
            Assert.False(Directory.Exists(_dir));
            _ = new SnapshotRunner(_dir, _ => { });
            Assert.True(Directory.Exists(_dir));
        }

        [Fact]
        public void Save_writes_named_png_returns_its_path_and_decodes()
        {
            var runner = new SnapshotRunner(_dir, _ => { });

            string path = runner.Save("hero", SampleRgba(), 2, 3);

            Assert.Equal(Path.Combine(_dir, "hero.png"), path);
            Assert.True(File.Exists(path));
            ImageResult decoded = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            Assert.Equal(2, decoded.Width);
            Assert.Equal(3, decoded.Height);
            Assert.Equal(SampleRgba(), decoded.Data);
        }

        [Fact]
        public void Save_logs_the_written_path()
        {
            var lines = new List<string>();
            var runner = new SnapshotRunner(_dir, lines.Add);

            string path = runner.Save("hero", SampleRgba(), 2, 3);

            Assert.Contains(path, lines);
        }

        [Fact]
        public void Save_increments_count()
        {
            var runner = new SnapshotRunner(_dir, _ => { });
            Assert.Equal(0, runner.Count);
            runner.Save("a", SampleRgba(), 2, 3);
            runner.Save("b", SampleRgba(), 2, 3);
            Assert.Equal(2, runner.Count);
        }

        [Fact]
        public void Done_logs_a_summary_referencing_the_output_dir()
        {
            var lines = new List<string>();
            var runner = new SnapshotRunner(_dir, lines.Add);
            runner.Save("a", SampleRgba(), 2, 3);
            lines.Clear();

            runner.Done();

            Assert.Single(lines);
            Assert.Contains(_dir, lines[0]);
            Assert.Contains("done", lines[0]);
        }

        [Fact]
        public void Default_logger_does_not_throw()
        {
            // No logger -> defaults to Console.WriteLine; just prove the path runs without error.
            var runner = new SnapshotRunner(_dir);
            runner.Save("a", SampleRgba(), 2, 3);
            runner.Done();
        }
    }
}
