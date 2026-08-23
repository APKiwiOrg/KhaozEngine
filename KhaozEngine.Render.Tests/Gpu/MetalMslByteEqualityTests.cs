using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-S3: the emitted MSL of every shipped program, hashed and pinned. Section 12.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para>
    /// WHAT THIS PINS, EXACTLY, and phase 2's own header is transplanted here for the third time because it is the
    /// thing readers get backwards. The table is baked from THIS path's own emission and every run compares this
    /// path against that bake, so what it detects is DRIFT: a shader source or a cross-compile option moving after
    /// the table was written. It compares nothing against the incumbent and cannot. <b>A wrong emission, baked
    /// once, passes forever.</b> What it does buy is that the options stop being a default nobody chose:
    /// <see cref="MslCrossCompilePin"/> chooses them, and a flip of one shows up here as every program moving at
    /// once.
    /// </para>
    /// <para>
    /// PARITY WITH THE INCUMBENT WAS THE OTHER ARTEFACT AND IT WAS A STANDING TEST, <c>MetalMslIncumbentParityTests</c>,
    /// which compared the two paths on every leg. It was deleted in 18.0.0 with the second path, and what it
    /// bought is gone with it: this file pins what the engine EMITS, and nothing checks that emission against an
    /// independent producer any more. That is the cost the removal design records as R2, and the honest reading
    /// of a green run here is regression detection rather than correctness evidence.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND ON EVERY LEG. The SPIRV-Cross native ships per RID and runs on macOS, Linux and Windows, so
    /// this is a plain <c>[Fact]</c> in the fast <c>ci.yml</c> loop rather than a <c>[GpuFact]</c>, and it runs on
    /// the legs that have no Metal at all. It is deliberately NOT named "Golden": that filter is for device-backed
    /// pixel comparisons.
    /// </para>
    /// <para>
    /// IT EMITS FRESH, EVERY RUN, AND THAT IS NOW LOAD-BEARING RATHER THAN INCIDENTAL. The Metal backend's own
    /// build path gained a disk cache in front of the emission
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/592">#592</see>), and this test deliberately
    /// does not go through it: it drives <see cref="SpirvCrossCompile"/> directly, which is a type in
    /// <c>KhaozEngine.Gpu</c> and cannot see a backend's cache at all. The reason is specific rather than
    /// hygienic. That cache's key covers the shader sources, the engine version and all three pinned option sets,
    /// so a source or an options change is a different entry and could never be answered stale. What the key does
    /// NOT name is the thing each pin's own header says pins the emission, the toolchain package version, which
    /// is <c>Silk.NET.Shaderc</c> and <c>Silk.NET.SPIRV.Cross</c> since 18.0.0 and was <c>Veldrid.SPIRV</c>
    /// before it. So within one engine version a cached entry can hold the PREVIOUS cross-compiler's output, and a
    /// drift test reading it would report no drift on exactly the change it exists to catch.
    /// <see cref="ThisTestEmitsFresh_AndCannotBeAnsweredFromADiskCache"/> is that pinned mechanically.
    /// </para>
    /// <para>
    /// BAKING. <c>KE_UPDATE_MSL_HASHES=1 dotnet test --filter MetalMslByteEquality</c> rewrites the table and
    /// passes. Do that ONLY when a shader source or the pinned options changed ON PURPOSE, and read the diff: a
    /// one-line GLSL edit moving one program's two hashes is expected, and the same edit moving thirty programs
    /// means the options moved instead, which invalidates BOTH Metal golden families: the incumbent's <c>metal</c>
    /// and the byte-identical <c>metal-native</c> copy of it the native backend has owned since <c>17.41.0</c>.
    /// </para>
    /// </summary>
    public sealed class MetalMslByteEqualityTests
    {
        /// <summary>Set to <c>1</c> to rewrite the checked-in table instead of asserting against it.</summary>
        const string UpdateEnvVar = "KE_UPDATE_MSL_HASHES";

        [Fact]
        public void EveryShippedProgramsEmittedMsl_MatchesItsPinnedHash()
        {
            Dictionary<string, string> emitted = EmitEverything();

            if (IsUpdating())
            {
                WriteTable(emitted);
                return;
            }

            Dictionary<string, string> pinned = ReadTable();
            Assert.True(pinned.Count > 0,
                $"The pinned MSL hash table at {TablePath()} is missing or empty. Bake it with "
                + $"{UpdateEnvVar}=1 and commit it. It is what decision M-S3 asserts against, so an absent table "
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
                "The cross-compiled MSL no longer matches its pinned hashes, which means either a shader source "
                + "changed, or the pinned cross-compile options changed (decision M-S3, "
                + $"{nameof(MslCrossCompilePin)}). ONE program moving is a shader edit. MANY programs moving at "
                + "once is the options, and that is the drift this test exists to catch, because it silently "
                + "invalidates both Metal golden families, the incumbent's and the byte-identical metal-native "
                + "copy of it the native backend has owned since 17.41.0. It also "
                + "moves the BINDING TABLE: the native backend reads its indices out of this exact text, so an "
                + "emission change is a binding change as well as a pixel one. The emitted MSL for each mismatch "
                + $"was written to Gpu/msl-evidence/ so the change can be read rather than guessed. Re-bake with "
                + $"{UpdateEnvVar}=1 once the change is understood and intended.\n"
                + string.Join("\n", problems));
        }

        /// <summary>
        /// THE PIN: NOTHING THIS TEST READS CAN HAVE COME OFF A DISK CACHE. Two checks, and neither is a proof of
        /// the general statement, which is what the class header's paragraph is for.
        /// <para>
        /// The first is structural and total: the emitter this file drives lives in <c>KhaozEngine.Gpu</c>, which
        /// does not reference the Metal package at all, so no cache in that package can answer for it. The second
        /// is a source scan of this one file for the backend types that WOULD reach a cache, which catches the
        /// realistic regression (someone switching this test onto the shipped build path) and would miss a
        /// rename. The needles are built from parts so the scan does not match its own text.
        /// </para>
        /// </summary>
        [Fact]
        public void ThisTestEmitsFresh_AndCannotBeAnsweredFromADiskCache()
        {
            string[] referenced = typeof(SpirvCrossCompile).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .ToArray();

            Assert.NotEmpty(referenced);
            Assert.DoesNotContain("KhaozEngine.Gpu.Metal", referenced, StringComparer.Ordinal);

            string source = File.ReadAllText(ThisFile());
            Assert.NotEmpty(source);

            foreach (string forbidden in new[] { "MetalShader" + "Build", "MetalMsl" + "Cache" })
            {
                Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                    "This test now names " + forbidden + ", which means it may be reading an emission off disk "
                    + "instead of producing one. The bake it compares against would then be pinned against a copy "
                    + "of itself for any change the cache key does not name, and the cross-compiler's own package "
                    + "version is exactly such a change. Emit through SpirvCrossCompile here, and leave the cache "
                    + "to the tests that exist to exercise it.");
            }
        }

        // ---- emission ------------------------------------------------------------------------------------

        static Dictionary<string, string> EmitEverything()
        {
            var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                CrossCompiledPair pair = PairMsl(program);
                emitted[program.Name + ".vertex"] = Sha256(pair.VertexSource);
                emitted[program.Name + ".fragment"] = Sha256(pair.FragmentSource);
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
                emitted[kernel.Name + ".compute"] = Sha256(ComputeMsl(kernel).ComputeSource);

            return emitted;
        }

        // The front end runs here rather than in a convenience member, because the MSL half deliberately has no
        // GLSL-source overload: the Metal backend needs the SPIR-V modules afterwards for its binding join.
        static CrossCompiledPair PairMsl(ShippedGraphicsProgram program)
            => SpirvCrossCompile.VertexFragmentToMsl(
                SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name),
                SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name),
                program.Name);

        static CrossCompiledCompute ComputeMsl(ShippedComputeKernel kernel)
            => SpirvCrossCompile.ComputeToMsl(
                SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name), kernel.Name);

        // Hashed as UTF-8 bytes of the emitted string, which is BYTE equality of the emission: the string is what
        // SPIRV-Cross produced, and Metal consumes it as UTF-8 through +stringWithUTF8String:.
        static string Sha256(string text)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        static string EmittedMsl(string key)
        {
            int dot = key.LastIndexOf('.');
            string name = key[..dot];
            string stage = key[(dot + 1)..];

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                if (!string.Equals(program.Name, name, StringComparison.Ordinal)) continue;
                CrossCompiledPair pair = PairMsl(program);
                return stage == "vertex" ? pair.VertexSource : pair.FragmentSource;
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                if (string.Equals(kernel.Name, name, StringComparison.Ordinal))
                    return ComputeMsl(kernel).ComputeSource;
            }
            return string.Empty;
        }

        // ---- the checked-in table ------------------------------------------------------------------------

        static bool IsUpdating()
        {
            string? value = Environment.GetEnvironmentVariable(UpdateEnvVar);
            return !string.IsNullOrWhiteSpace(value) && value.Trim() is "1" or "true" or "yes" or "on";
        }

        /// <summary>The table next to this source file, located by <see cref="CallerFilePathAttribute"/> so it
        /// does not depend on <c>dotnet test</c>'s working directory.</summary>
        static string TablePath([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "msl-hashes", "metal-msl.sha256.txt");

        static string EvidenceDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "msl-evidence");

        /// <summary>This source file, for the pin above. Located the same way the hash table is, so it does not
        /// depend on <c>dotnet test</c>'s working directory.</summary>
        static string ThisFile([CallerFilePath] string thisFile = "") => thisFile;

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
            text.Append("# SHA-256 of the MSL that SPIRV-Cross emits for every shipped shader program, under\n")
                .Append("# the cross-compile options pinned in KhaozEngine.Gpu/Internal/MslCrossCompilePin.cs\n")
                .Append("# (decision M-S3). One line per emitted stage: <program>.<stage> <sha256>.\n")
                .Append("#\n")
                .Append("# Re-bake with KE_UPDATE_MSL_HASHES=1 and read the diff. One program moving is a shader\n")
                .Append("# edit. Thirty moving at once is the options, which invalidates the shared Metal golden\n")
                .Append("# family and ALSO moves the native backend's binding table, since it reads its indices\n")
                .Append("# out of this exact text.\n")
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
                string msl = EmittedMsl(key);
                if (msl.Length == 0) return;
                string dir = EvidenceDir();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, key + ".metal"), msl);
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
