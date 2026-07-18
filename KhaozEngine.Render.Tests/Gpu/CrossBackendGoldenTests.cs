using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Cross-backend consistency guard for the committed golden grids. Each per-backend golden
    /// (<c>goldens/&lt;scene&gt;.&lt;backend&gt;.txt</c>) is normally only verified against ITSELF on its own
    /// backend, so a bad bake on one backend can drift from the others and pass silently - which is exactly what
    /// happened once (a D3D11 golden had a mesh rendered ~0.58 brighter than Metal for several releases, unseen).
    ///
    /// This headless test (no GPU - it just reads the committed grid files) compares every scene's goldens across
    /// the backends that have one, and fails if any pair diverges beyond a GENEROUS threshold. Backends really do
    /// differ a little (software rasterizers like WARP / lavapipe do not match Apple Metal pixel-for-pixel), so
    /// the threshold is deliberately loose - far above the observed legitimate divergence (~0.04 Metal-vs-WARP)
    /// and the 0.06 per-backend verify tolerance, but well below a gross bad-bake (the 0.58 case). It catches
    /// "one backend's reference is wrong", not normal filtering noise.
    /// </summary>
    public class CrossBackendGoldenTests
    {
        /// <summary>
        /// Max allowed per-channel divergence between any two backends' goldens for the same scene. Current
        /// committed goldens sit at ~0.044 (Metal vs D3D11); the bad bake that motivated this was ~0.58. 0.20
        /// leaves generous headroom for legitimate software-rasterizer / runner-driver differences while still
        /// catching a grossly divergent (wrong) per-backend reference.
        /// </summary>
        const float MaxCrossBackendDiff = 0.20f;

        [Fact]
        public void Committed_goldens_agree_across_backends_within_a_generous_tolerance()
        {
            string dir = GoldensDir();
            Assert.True(Directory.Exists(dir), $"goldens directory not found at {dir}");

            // Group committed grids by scene: file name is <scene>.<backend>.txt.
            var byScene = new Dictionary<string, Dictionary<string, float[]>>();
            foreach (string path in Directory.EnumerateFiles(dir, "*.txt"))
            {
                string stem = Path.GetFileNameWithoutExtension(path);   // <scene>.<backend>
                int dot = stem.LastIndexOf('.');
                if (dot <= 0) continue;
                string scene = stem.Substring(0, dot);
                string backend = stem.Substring(dot + 1);
                if (!byScene.TryGetValue(scene, out var grids)) byScene[scene] = grids = new();
                grids[backend] = ParseGrid(File.ReadAllText(path));
            }

            Assert.NotEmpty(byScene);   // a guard with no goldens to check is a config error, not a pass.

            var failures = new StringBuilder();
            int comparedPairs = 0;

            foreach ((string scene, var grids) in byScene.OrderBy(kv => kv.Key))
            {
                // Need at least two backends to cross-check; a single-backend scene is skipped (nothing to
                // compare it against yet - e.g. a backend whose golden has not been baked).
                var backends = grids.Keys.OrderBy(b => b).ToList();
                if (backends.Count < 2) continue;

                for (int i = 0; i < backends.Count; i++)
                    for (int j = i + 1; j < backends.Count; j++)
                    {
                        float[] a = grids[backends[i]], b = grids[backends[j]];
                        comparedPairs++;
                        if (a.Length != b.Length)
                        {
                            failures.Append($"  {scene}: {backends[i]} has {a.Length / 3} cells but {backends[j]} has {b.Length / 3} - re-bake one.\n");
                            continue;
                        }

                        float worst = 0f; int worstCell = -1, worstCh = 0;
                        for (int k = 0; k < a.Length; k++)
                        {
                            float d = Math.Abs(a[k] - b[k]);
                            if (d > worst) { worst = d; worstCell = k / 3; worstCh = k % 3; }
                        }
                        if (worst > MaxCrossBackendDiff)
                        {
                            string chN = worstCh == 0 ? "R" : worstCh == 1 ? "G" : "B";
                            int cx = worstCell % GoldenCompare.GridW, cy = worstCell / GoldenCompare.GridW;
                            failures.Append($"  {scene}: {backends[i]} vs {backends[j]} diverge by {worst:0.###} ")
                                    .Append($"(> {MaxCrossBackendDiff:0.##}) at cell ({cx},{cy}) {chN}. ")
                                    .Append("One backend's golden is likely a bad bake - check which matches the scene.\n");
                        }
                    }
            }

            Assert.True(comparedPairs > 0,
                "no scene had goldens on two or more backends to cross-check (only one backend baked so far).");

            if (failures.Length > 0)
                Assert.Fail("Committed per-backend goldens diverge beyond the cross-backend tolerance:\n" + failures);
        }

        // Parse a committed grid file (# header + one "r g b" line per cell) to a flat float array.
        static float[] ParseGrid(string text)
        {
            var vals = new List<float>(GoldenCompare.GridW * GoldenCompare.GridH * 3);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) continue;
                vals.Add(float.Parse(parts[0], CultureInfo.InvariantCulture));
                vals.Add(float.Parse(parts[1], CultureInfo.InvariantCulture));
                vals.Add(float.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            return vals.ToArray();
        }

        // The committed goldens live next to GoldenCompare.cs (Gpu/goldens), independent of the working dir.
        static string GoldensDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "goldens");
    }
}
