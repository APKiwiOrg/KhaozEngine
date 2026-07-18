using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Snapshot;
using Xunit;

namespace KhaozEngine.Tests.Snapshot
{
    public class SnapshotHostTests
    {
        static byte[] Pixel() => new byte[] { 12, 34, 56, 255 };  // 1x1 RGBA

        [Fact]
        public void Run_uses_args0_as_the_output_directory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-snaphost-" + Guid.NewGuid().ToString("N"));
            var lines = new List<string>();
            try
            {
                string outDir = SnapshotHost.Run(new[] { dir }, r => r.Save("only", Pixel(), 1, 1), lines.Add);

                Assert.Equal(dir, outDir);
                Assert.True(File.Exists(Path.Combine(dir, "only.png")));
                Assert.Contains(lines, l => l.Contains("done") && l.Contains(dir));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Run_falls_back_to_the_deterministic_default_dir_when_no_args()
        {
            // No args -> DefaultOutDir (deterministic, no timestamp). Same value on every call.
            Assert.Equal(SnapshotHost.DefaultOutDir, SnapshotHost.DefaultOutDir);

            string outDir = SnapshotHost.Run(Array.Empty<string>(), r => r.Save("only", Pixel(), 1, 1), _ => { });

            Assert.Equal(SnapshotHost.DefaultOutDir, outDir);
            Assert.True(File.Exists(Path.Combine(outDir, "only.png")));
        }

        [Fact]
        public void Main_returns_zero()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-snaphost-" + Guid.NewGuid().ToString("N"));
            try
            {
                int code = SnapshotHost.Main(new[] { dir }, r => r.Save("only", Pixel(), 1, 1), _ => { });
                Assert.Equal(0, code);
                Assert.True(File.Exists(Path.Combine(dir, "only.png")));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }
    }
}
