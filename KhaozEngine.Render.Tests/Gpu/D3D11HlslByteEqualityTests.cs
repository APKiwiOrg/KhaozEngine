using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION S3: the emitted HLSL of every shipped program, hashed and pinned. Section 8.2 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>.
    ///
    /// <para>
    /// WHAT THIS PINS, EXACTLY. The table is baked from THIS path's own emission and every run compares this
    /// path against that bake, so what the test detects is DRIFT: a shader source or a cross-compile option
    /// moving after the table was written. It compares nothing against the incumbent Veldrid path and cannot, so
    /// reading a green run as parity evidence reads it backwards. A wrong emission, baked once, passes forever.
    /// What it does buy is that the options stop being a default nobody chose: <see cref="HlslCrossCompilePin"/>
    /// chooses them, and a flip of one shows up here as every program moving at once.
    /// </para>
    /// <para>
    /// PARITY WITH THE INCUMBENT IS A SEPARATE, HISTORICAL FACT, measured once at review time on 2026-08-03: all
    /// 34 shipped graphics programs emitted byte-identical HLSL under this path and under a faithful replication
    /// of the incumbent's <c>CreateFromSpirv</c> call. That measurement is what lets the 36 committed Direct3D 11
    /// goldens carry over without a rebake. It is not what this test asserts, and it is not re-run on any leg.
    /// This test's job starts where that measurement ended: nothing has moved since.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND ON EVERY LEG. The SPIRV-Cross native ships per RID and runs on macOS and Linux (which
    /// <c>SpirvCrossCompileTests</c> already relies on), so this is a plain <c>[Fact]</c> in the fast
    /// <c>ci.yml</c> loop rather than a <c>[GpuFact]</c>. It is deliberately NOT named "Golden": the golden
    /// filter is for device-backed pixel comparisons, and this needs to run everywhere, including the legs that
    /// have no device at all.
    /// </para>
    /// <para>
    /// BAKING. <c>KE_UPDATE_HLSL_HASHES=1 dotnet test --filter D3D11HlslByteEquality</c> rewrites the table and
    /// passes. Do that ONLY when a shader source or the pinned options changed ON PURPOSE, and read the diff: a
    /// one-line GLSL edit moving one program's two hashes is expected, and the same edit moving thirty programs
    /// means the options moved instead.
    /// </para>
    /// <para>
    /// IF THIS FAILS ON A CI LEG BUT PASSES LOCALLY, there are exactly two causes and the failure message names
    /// both. Either the pinned options changed (in which case it fails everywhere, and the fix is to re-bake
    /// deliberately), or the SPIRV-Cross native emits different text on that runner's RID than on the machine the
    /// table was baked on (in which case only some legs fail, and it is not a shader problem at all). The second
    /// has not been observed and is not expected, since the native is built from one source per RID, but it is
    /// the reason the emitted HLSL is dumped to <c>Gpu/hlsl-evidence/</c> on failure rather than only the hashes:
    /// with the text in hand the two cases are one diff apart.
    /// </para>
    /// </summary>
    public sealed class D3D11HlslByteEqualityTests
    {
        /// <summary>Set to <c>1</c> to rewrite the checked-in table instead of asserting against it.</summary>
        const string UpdateEnvVar = "KE_UPDATE_HLSL_HASHES";

        [Fact]
        public void EveryShippedProgramsEmittedHlsl_MatchesItsPinnedHash()
        {
            Dictionary<string, string> emitted = EmitEverything();

            if (IsUpdating())
            {
                WriteTable(emitted);
                return;
            }

            Dictionary<string, string> pinned = ReadTable();
            Assert.True(pinned.Count > 0,
                $"The pinned HLSL hash table at {TablePath()} is missing or empty. Bake it with "
                + $"{UpdateEnvVar}=1 and commit it. It is what decision S3 asserts against, so an absent table "
                + "is an unasserted claim rather than a passing test.");

            var problems = new List<string>();
            foreach ((string key, string hash) in emitted.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!pinned.TryGetValue(key, out string? want))
                {
                    problems.Add($"  {key}: not in the table. A program was added to "
                        + $"{nameof(D3D11ShaderProgramCatalog)} without a bake.");
                }
                else if (!string.Equals(want, hash, StringComparison.Ordinal))
                {
                    problems.Add($"  {key}: emitted {hash}, table says {want}.");
                    DumpEvidence(key);
                }
            }
            foreach (string orphan in pinned.Keys.Where(k => !emitted.ContainsKey(k))
                         .OrderBy(k => k, StringComparer.Ordinal))
            {
                problems.Add($"  {orphan}: in the table but no longer emitted. A program was removed from "
                    + $"{nameof(D3D11ShaderProgramCatalog)} without a bake.");
            }

            Assert.True(problems.Count == 0,
                "The cross-compiled HLSL no longer matches its pinned hashes, which means either a shader source "
                + "changed, or the pinned cross-compile options changed (decision S3, "
                + $"{nameof(HlslCrossCompilePin)}). ONE program moving is a shader edit. MANY programs moving at "
                + "once is the options, and that is the drift this test exists to catch, because it silently "
                + "invalidates the shared Direct3D 11 golden family. The emitted HLSL for each mismatch was "
                + $"written to Gpu/hlsl-evidence/ so the change can be read rather than guessed. Re-bake with "
                + $"{UpdateEnvVar}=1 once the change is understood and intended.\n"
                + string.Join("\n", problems));
        }

        /// <summary>
        /// The catalog and the table agree on WHICH programs exist, asserted separately from the hashes so a
        /// forgotten row reads as "the catalog and the table disagree" rather than as a shader regression. The
        /// count is stated so a silently emptied catalog cannot pass by agreeing with an emptied table.
        /// </summary>
        [Fact]
        public void TheCatalogCoversEveryShippedProgram()
        {
            var graphics = D3D11ShaderProgramCatalog.GraphicsPrograms().ToArray();
            var compute = D3D11ShaderProgramCatalog.ComputeKernels().ToArray();

            // 37 non-test CreateShadersFromSpirv call sites, 35 distinct source pairs (the Line pair is created
            // three times). R5's tile-ground pass is the newest, and the count moves with every pipeline the
            // renderers gain. Two compute kernels across the four reachable cascade resolutions.
            Assert.Equal(35, graphics.Length);
            Assert.Equal(8, compute.Length);

            Assert.Equal(graphics.Length, graphics.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(compute.Length, compute.Select(k => k.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(graphics, p =>
            {
                Assert.StartsWith("#version 450", p.VertexGlsl.TrimStart(), StringComparison.Ordinal);
                Assert.StartsWith("#version 450", p.FragmentGlsl.TrimStart(), StringComparison.Ordinal);
            });
            Assert.All(compute, k =>
                Assert.StartsWith("#version 450", k.ComputeGlsl.TrimStart(), StringComparison.Ordinal));
        }

        // ---- emission ------------------------------------------------------------------------------------

        static Dictionary<string, string> EmitEverything()
        {
            var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                    program.VertexGlsl, program.FragmentGlsl, program.Name);
                emitted[program.Name + ".vertex"] = Sha256(pair.VertexSource);
                emitted[program.Name + ".fragment"] = Sha256(pair.FragmentSource);
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                CrossCompiledCompute compute = SpirvCrossCompile.GlslComputeToHlsl(kernel.ComputeGlsl, kernel.Name);
                emitted[kernel.Name + ".compute"] = Sha256(compute.ComputeSource);
            }
            return emitted;
        }

        // Hashed as UTF-8 bytes of the emitted string. That is BYTE equality of the emission, which is what S3
        // asks for: the string is what SPIRV-Cross produced, and FXC consumes it as ASCII.
        static string Sha256(string text)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        static string EmittedHlsl(string key)
        {
            int dot = key.LastIndexOf('.');
            string name = key.Substring(0, dot);
            string stage = key.Substring(dot + 1);

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                if (!string.Equals(program.Name, name, StringComparison.Ordinal)) continue;
                CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
                    program.VertexGlsl, program.FragmentGlsl, program.Name);
                return stage == "vertex" ? pair.VertexSource : pair.FragmentSource;
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                if (string.Equals(kernel.Name, name, StringComparison.Ordinal))
                    return SpirvCrossCompile.GlslComputeToHlsl(kernel.ComputeGlsl, kernel.Name).ComputeSource;
            }
            return string.Empty;
        }

        // ---- the checked-in table ------------------------------------------------------------------------

        static bool IsUpdating()
        {
            string? value = Environment.GetEnvironmentVariable(UpdateEnvVar);
            return !string.IsNullOrWhiteSpace(value)
                && value.Trim() is "1" or "true" or "yes" or "on";
        }

        /// <summary>The table next to this source file, located by <see cref="CallerFilePathAttribute"/> so it
        /// does not depend on <c>dotnet test</c>'s working directory. Same shape <c>GoldenCompare</c> uses for
        /// the committed goldens.</summary>
        static string TablePath([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "hlsl-hashes", "d3d11-hlsl.sha256.txt");

        static string EvidenceDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "hlsl-evidence");

        static Dictionary<string, string> ReadTable()
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            string path = TablePath();
            if (!File.Exists(path)) return table;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                string[] parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) table[parts[0]] = parts[1];
            }
            return table;
        }

        static void WriteTable(Dictionary<string, string> emitted)
        {
            string path = TablePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var text = new StringBuilder();
            text.Append("# SHA-256 of the HLSL that SPIRV-Cross emits for every shipped shader program, under\n")
                .Append("# the cross-compile options pinned in KhaozEngine.Gpu/Internal/HlslCrossCompilePin.cs\n")
                .Append("# (decision S3). One line per emitted stage: <program>.<stage> <sha256>.\n")
                .Append("#\n")
                .Append("# Re-bake with KE_UPDATE_HLSL_HASHES=1 and read the diff. One program moving is a\n")
                .Append("# shader edit. Thirty moving at once is the options, which invalidates the shared\n")
                .Append("# Direct3D 11 golden family and is the drift D3D11HlslByteEqualityTests exists to catch.\n")
                .Append("#\n")
                .Append("# Entries: ")
                .Append(emitted.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');

            foreach ((string key, string hash) in emitted.OrderBy(e => e.Key, StringComparer.Ordinal))
                text.Append(key).Append(' ').Append(hash).Append('\n');

            File.WriteAllText(path, text.ToString());
        }

        static void DumpEvidence(string key)
        {
            try
            {
                string hlsl = EmittedHlsl(key);
                if (hlsl.Length == 0) return;
                string dir = EvidenceDir();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, key + ".hlsl"), hlsl);
            }
            catch (IOException)
            {
                // Evidence is a convenience. A read-only or full workspace must not turn a hash mismatch into a
                // different, less informative failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
