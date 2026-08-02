using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Tolerance-based image regression: downsamples a raw RGBA buffer to a small grid of average RGB per cell
    /// and either WRITES a committed reference grid (when <c>KE_UPDATE_GOLDENS=1</c>) or COMPARES against it with
    /// a per-channel tolerance. Robust to minor driver noise; a real shader/UBO/blend/winding regression moves a
    /// cell well past the tolerance.
    /// <para>
    /// GOLDEN NAMING CONTRACT: the cross-platform GPU matrix
    /// (<c>.github/workflows/cross-platform-gpu.yml</c>) selects tests with
    /// <c>--filter FullyQualifiedName~Golden</c>, so a GPU test is run on every backend IFF its fully-qualified
    /// name contains "Golden". Never rename a golden test to drop "Golden" from its class or method name: it would
    /// silently vanish from the CI filter and stop being verified cross-backend, with no red. Two flavors both
    /// carry "Golden" and both are run by the matrix: (a) committed-grid goldens that diff against a per-backend
    /// reference grid via <see cref="AssertOrUpdate(string,byte[],int,int)"/>, and (b) property/invariant "goldens"
    /// (e.g. <c>SplatTerrainGoldenTests</c>) that assert thresholds/invariants on the rendered pixels instead of a
    /// committed grid. See <c>docs/CROSS-PLATFORM.md</c> for the full contract.
    /// </para>
    /// </summary>
    internal static class GoldenCompare
    {
        /// <summary>Downsample grid width in cells.</summary>
        public const int GridW = KhaozEngine.Imaging.GoldenGrid.DefaultGridW;
        /// <summary>Downsample grid height in cells.</summary>
        public const int GridH = KhaozEngine.Imaging.GoldenGrid.DefaultGridH;
        /// <summary>Per-channel absolute-difference tolerance (channels are 0..1).</summary>
        public const float Tolerance = KhaozEngine.Imaging.GoldenGrid.DefaultTolerance;

        /// <summary>
        /// Downsample <paramref name="rgba"/> (raw RGBA8, <paramref name="w"/>×<paramref name="h"/>) to a
        /// <see cref="GridW"/>×<see cref="GridH"/> grid of average RGB per cell as floats 0..1, row-major,
        /// 3 floats per cell. Delegates to <see cref="KhaozEngine.Imaging.GoldenGrid.Downsample"/>.
        /// </summary>
        public static float[] Downsample(byte[] rgba, int w, int h)
            => KhaozEngine.Imaging.GoldenGrid.Downsample(rgba, w, h, GridW, GridH);

        /// <summary>
        /// Capture-and-check entry point. Downsamples <paramref name="rgba"/>; when <c>KE_UPDATE_GOLDENS=1</c>
        /// writes the reference for <paramref name="name"/> and skips the assert, otherwise compares against the
        /// committed reference and fails listing the worst-offending cells. On failure / missing golden / bake it
        /// also writes viewable PNG evidence (see the internal overload) so the outcome can be eyeballed without
        /// re-running: the got frame, the reconstructed want image, and a per-cell diff heat map.
        /// </summary>
        public static void AssertOrUpdate(string name, byte[] rgba, int w, int h)
        {
            KhaozEngine.Gpu.GpuBackendKind kind = KhaozEngine.Gpu.GpuBackendSelector.Select();
            AssertOrUpdate(name, rgba, w, h, GoldenDir(), EvidenceDir(), GoldenBackendToken(kind),
                Environment.GetEnvironmentVariable("KE_UPDATE_GOLDENS") == "1",
                BakeRefusal(kind, Environment.GetEnvironmentVariable(FamilyOverrideEnvVar) == "1"));
        }

        /// <summary>The env var that lets a bake write into a golden family the running backend does not OWN.</summary>
        public const string FamilyOverrideEnvVar = "KE_GOLDEN_FAMILY_OVERRIDE";

        /// <summary>
        /// The golden FAMILY a backend's references live in: the <c>&lt;backend&gt;</c> token in
        /// <c>&lt;name&gt;.&lt;backend&gt;.txt</c>. Usually just the kind's own lower-cased name, which is what the
        /// two filename sites used to derive inline, one each.
        /// <para>
        /// The exception is the whole point (decision I3 of
        /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>).
        /// <see cref="KhaozEngine.Gpu.GpuBackendKind.Direct3D11Native"/> is a second IMPLEMENTATION of Direct3D 11,
        /// not a second API, so it renders the same images on the same rasterizer and SHARES the
        /// <c>direct3d11</c> family. That sharing is not a convenience: holding the native backend to the
        /// incumbent's already-committed references, unmodified, at the existing tolerance, is the strongest free
        /// proof the whole port has, and deriving the token from the enum name would have thrown it away by
        /// orphaning 36 goldens behind a name nothing had ever baked.
        /// </para>
        /// <para>
        /// No discard that guesses. An appended kind lands on the throwing arm rather than silently inventing a
        /// family nobody baked, and <c>GpuBackendKindAppendAuditTests</c> walks every member so the failure
        /// arrives from a device-free test rather than from a GPU leg.
        /// </para>
        /// </summary>
        public static string GoldenBackendToken(KhaozEngine.Gpu.GpuBackendKind kind) => kind switch
        {
            KhaozEngine.Gpu.GpuBackendKind.Metal => "metal",
            KhaozEngine.Gpu.GpuBackendKind.Vulkan => "vulkan",
            KhaozEngine.Gpu.GpuBackendKind.Direct3D11 => "direct3d11",
            KhaozEngine.Gpu.GpuBackendKind.Direct3D11Native => "direct3d11",
            KhaozEngine.Gpu.GpuBackendKind.OpenGL => "opengl",
            _ => throw new NotSupportedException(
                $"No golden family is decided for {kind}. Appending a GpuBackendKind member means deciding "
                + "whether it owns a family or shares one, because the filename is derived from this and nothing "
                + "else fails when it is wrong: the run just compares against a golden that does not exist."),
        };

        /// <summary>
        /// Why <c>KE_UPDATE_GOLDENS</c> must NOT write on <paramref name="kind"/>, or null when baking is allowed.
        /// <para>
        /// A backend that is a GUEST in another backend's family (its token is not its own name) would, on a bake,
        /// overwrite both the reference it is itself being checked against and the owning implementation's, from a
        /// run that proves nothing about either. There is no way to notice afterwards: the file it wrote is
        /// exactly the file it would have compared against. So the refusal is the guard, and
        /// <see cref="FamilyOverrideEnvVar"/> is the deliberate way past it for the one case that is legitimate,
        /// moving the shared family on purpose.
        /// </para>
        /// </summary>
        public static string? BakeRefusal(KhaozEngine.Gpu.GpuBackendKind kind, bool familyOverride)
        {
            if (familyOverride) return null;

            string token = GoldenBackendToken(kind);
            if (string.Equals(token, kind.ToString(), StringComparison.OrdinalIgnoreCase)) return null;

            return $"KE_UPDATE_GOLDENS refused on {kind}: it does not own the '{token}' golden family, it shares "
                + "it. Baking here would overwrite the very references this backend is being CHECKED against, and "
                + "the owning implementation's with them, which is the one proof the shared family exists to buy. "
                + $"Re-bake on the backend that owns '{token}', or set {FamilyOverrideEnvVar}=1 if you really do "
                + "mean to move the shared family.";
        }

        /// <summary>
        /// Core compare/bake logic, parameterized on directories + backend so it is testable against throwaway
        /// temp dirs (never the committed goldens dir, never a process-wide env-var mutation). The public
        /// <see cref="AssertOrUpdate(string,byte[],int,int)"/> resolves those from the environment and delegates
        /// here. Golden text lives at <c>&lt;goldenDir&gt;/&lt;name&gt;.&lt;backend&gt;.txt</c>; evidence PNGs at
        /// <c>&lt;evidenceDir&gt;/&lt;name&gt;.&lt;backend&gt;.{got,want,diff,bake}.png</c>.
        /// </summary>
        internal static void AssertOrUpdate(string name, byte[] rgba, int w, int h,
            string goldenDir, string evidenceDir, string backend, bool updateGoldens,
            string? bakeRefusal = null)
        {
            float[] grid = Downsample(rgba, w, h);
            string path = Path.Combine(goldenDir, name + "." + backend + ".txt");
            if (updateGoldens)
            {
                // Fails rather than quietly degrading to a compare. The operator asked to overwrite a reference
                // and must be told they did not get one, or the next run's green is read as the bake having
                // worked.
                if (bakeRefusal != null) Assert.Fail(bakeRefusal);
                Directory.CreateDirectory(goldenDir);
                File.WriteAllText(path, Serialize(grid));
                // Evidence: the full-res capture, so CI bake artifacts are viewable.
                WriteEvidence(evidenceDir, name, backend, "bake", rgba, w, h);
                return;
            }

            if (!File.Exists(path))
            {
                // Write the captured frame so a brand-new scene can be eyeballed before its first bake.
                string gotPath = WriteEvidence(evidenceDir, name, backend, "got", rgba, w, h);
                Assert.Fail(
                    $"golden '{name}' missing at {path}. Run with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 to generate it. " +
                    $"Captured frame written to: {gotPath}");
            }
            float[] golden = Deserialize(File.ReadAllText(path));
            Assert.True(golden.Length == grid.Length,
                $"golden '{name}' has {golden.Length / 3} cells, expected {grid.Length / 3}. Re-bake with KE_UPDATE_GOLDENS=1.");

            // Collect the worst offenders for a useful failure message.
            var comparison = KhaozEngine.Imaging.GoldenGrid.Compare(grid, golden, Tolerance);
            var offenders = comparison.Offenders;
            float worst = comparison.WorstDiff;

            if (offenders.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"golden '{name}' regressed: {offenders.Count} channel(s) over tol {Tolerance:0.###} ")
                  .Append($"(worst abs diff {worst:0.###}). Top cells (cx,cy ch got/want):\n");
                int show = Math.Min(8, offenders.Count);
                for (int k = 0; k < show; k++)
                {
                    var off = offenders[k];
                    int cx = off.Cell % GridW, cy = off.Cell / GridW;
                    string chN = off.Channel == 0 ? "R" : off.Channel == 1 ? "G" : "B";
                    sb.Append($"  ({cx},{cy}) {chN} got {off.Got:0.###} want {off.Want:0.###} (diff {off.Diff:0.###})\n");
                }

                // Viewable evidence: the captured frame, the golden reconstructed as an image, and a diff heat map.
                string gotPath = WriteEvidence(evidenceDir, name, backend, "got", rgba, w, h);
                string wantPath = WriteEvidence(evidenceDir, name, backend, "want", GridToImage(golden, w, h), w, h);
                string diffPath = WriteEvidence(evidenceDir, name, backend, "diff", DiffHeatMap(grid, golden, w, h), w, h);
                sb.Append("Evidence PNGs:\n")
                  .Append("  got:  ").Append(gotPath).Append('\n')
                  .Append("  want: ").Append(wantPath).Append('\n')
                  .Append("  diff: ").Append(diffPath).Append('\n');
                sb.Append("Re-bake intentionally with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 if the change is expected.");
                Assert.Fail(sb.ToString());
            }
        }

        /// <summary>Encode <paramref name="rgba"/> (w x h RGBA8) to <c>&lt;dir&gt;/&lt;name&gt;.&lt;backend&gt;.&lt;kind&gt;.png</c> and return the path.</summary>
        static string WriteEvidence(string dir, string name, string backend, string kind, byte[] rgba, int w, int h)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, $"{name}.{backend}.{kind}.png");
            KhaozEngine.Imaging.PngWriter.Save(p, rgba, w, h);
            return p;
        }

        /// <summary>
        /// Reconstruct a <see cref="GridW"/>x<see cref="GridH"/> golden grid (3 floats/cell, 0..1) into a
        /// <paramref name="w"/>x<paramref name="h"/> RGBA8 image, each cell painted as a flat nearest-neighbour
        /// block, so it lines up dimensionally with the captured frame. Delegates to
        /// <see cref="KhaozEngine.Imaging.GoldenGrid.GridToImage"/>.
        /// </summary>
        static byte[] GridToImage(float[] grid, int w, int h)
            => KhaozEngine.Imaging.GoldenGrid.GridToImage(grid, w, h, GridW, GridH);

        /// <summary>
        /// Build a <paramref name="w"/>x<paramref name="h"/> per-cell diff heat map: black for zero diff, scaling
        /// to full red at/above 2x <see cref="Tolerance"/> (max channel abs diff of the cell). Cells over
        /// tolerance are painted full-saturation red with a black inner border so they are unmistakable.
        /// Delegates to <see cref="KhaozEngine.Imaging.GoldenGrid.DiffHeatMap"/>.
        /// </summary>
        static byte[] DiffHeatMap(float[] got, float[] golden, int w, int h)
            => KhaozEngine.Imaging.GoldenGrid.DiffHeatMap(got, golden, w, h, GridW, GridH, Tolerance);

        static string Serialize(float[] grid)
            => KhaozEngine.Imaging.GoldenGrid.Serialize(grid, GridW, GridH);

        static float[] Deserialize(string text)
            => KhaozEngine.Imaging.GoldenGrid.Deserialize(text);

        /// <summary>
        /// Resolve <c>Gpu/goldens/&lt;name&gt;.&lt;backend&gt;.txt</c> next to this source file, where
        /// <c>&lt;backend&gt;</c> is the active <see cref="KhaozEngine.Gpu.GpuBackendSelector.Select()"/> result
        /// mapped through <see cref="GoldenBackendToken"/> (metal / vulkan / direct3d11 / opengl). Each rendering
        /// API gets its own reference grid because a software rasterizer (lavapipe, WARP) won't match Metal
        /// pixel-for-pixel, and two implementations of ONE api share a grid for the opposite reason. Using
        /// <see cref="CallerFilePathAttribute"/> makes the path independent of <c>dotnet test</c>'s working
        /// directory and the build output layout, so generated references and checks always hit the committed
        /// source tree.
        /// </summary>
        public static string GoldenPath(string name, [CallerFilePath] string thisFile = "")
        {
            // Through GoldenBackendToken, the same as the compare/bake site above. These are the TWO places the
            // kind becomes a filename, and a copy that derived it differently would have a bake path and a compare
            // path disagreeing about which family a backend belongs to.
            string backend = GoldenBackendToken(KhaozEngine.Gpu.GpuBackendSelector.Select());
            return Path.Combine(GoldenDir(thisFile), name + "." + backend + ".txt");
        }

        /// <summary>The committed goldens directory next to this source file (<c>Gpu/goldens/</c>).</summary>
        static string GoldenDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "goldens");

        /// <summary>
        /// Where failure-evidence PNGs are written: the <c>KE_GOLDEN_EVIDENCE_DIR</c> env var if set, else
        /// <c>Gpu/goldens-evidence/</c> next to this source file (via <see cref="CallerFilePathAttribute"/>, the
        /// same working-dir-independent technique as <see cref="GoldenPath"/>). The default dir is gitignored.
        /// </summary>
        static string EvidenceDir([CallerFilePath] string thisFile = "")
        {
            string? env = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Path.GetDirectoryName(thisFile)!, "goldens-evidence");
        }
    }
}
