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
    /// DECISION V-T5: the SPIR-V of every shipped program, hashed and pinned. Section 12.1 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>, work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para>
    /// WHAT THIS PINS, EXACTLY, AND THE WARNING IS TRANSPLANTED FROM THE DIRECT3D 11 TEST RATHER THAN
    /// REDISCOVERED. The table is baked from THIS path's own emission and every run compares this path against
    /// that bake, so what the test detects is DRIFT: a shader source or a front-end option moving after the table
    /// was written. It compares nothing against the incumbent Veldrid path and cannot, so reading a green run as
    /// parity evidence reads it backwards. A wrong emission, baked once, passes forever. What it does buy is that
    /// the options stop being a default nobody chose: <see cref="SpirvFrontEndPin"/> chooses them, and a flip of
    /// one shows up here as every program moving at once.
    /// </para>
    /// <para>
    /// PARITY WITH THE INCUMBENT WAS A SEPARATE ASSERTION AND IT LIVED NEXT DOOR, in
    /// <c>VulkanSpirvIncumbentParityTests</c>, deleted in 18.0.0 with the incumbent. It is the one that licensed
    /// carrying the committed
    /// <c>vulkan</c> goldens over to the native backend without a rebake. It was measured once, in process, on
    /// 2026-08-08: all 34 shipped graphics programs (68 stage compiles) and all 8 shipped compute kernels, 76
    /// stage emissions in total, compiled to BYTE-IDENTICAL SPIR-V under this path and under a faithful
    /// replication of the incumbent's own SPIR-V production, which is
    /// <c>SpirvCompilation.CompileGlslToSpirv(source, fileName: null, stage, GlslCompileOptions.Default)</c>, the
    /// call <c>Veldrid.SPIRV</c>'s <c>CreateFromSpirv</c> makes on a Vulkan device. 76 of 76 equal, 0 mismatches.
    /// The one difference between the two call shapes is the diagnostic FILE NAME, which the incumbent leaves null
    /// and this engine sets, and the measurement is what establishes that it never reaches the module while
    /// <see cref="SpirvFrontEndPin.Debug"/> is false. That measurement is recorded in section 12.1 of the design
    /// and stands as the historical record of what licensed the goldens carrying over. That comparison ran on
    /// every leg as a standing test until 18.0.0, because the equality was not true by construction: the pin
    /// governed the engine's own front-end seat and the incumbent kept the library defaults. This test's job
    /// starts where that measurement ended: nothing has moved since.
    /// </para>
    /// <para>
    /// DEVICE-FREE AND ON EVERY LEG. The front end runs on the CPU through a native that ships per RID and already
    /// runs on macOS and Linux, so this is a plain <c>[Fact]</c> in the fast <c>ci.yml</c> loop rather than a
    /// <c>[GpuFact]</c>. It is deliberately NOT named "Golden": the golden filter is for device-backed pixel
    /// comparisons, and this needs to run everywhere, including the legs with no Vulkan loader at all.
    /// </para>
    /// <para>
    /// BAKING. <c>KE_UPDATE_SPIRV_HASHES=1 dotnet test --filter VulkanSpirvByteEquality</c> rewrites the table and
    /// passes. Do that ONLY when a shader source or the pinned front-end options changed ON PURPOSE, and read the
    /// diff: a one-line GLSL edit moving one program's hash is expected, and the same edit moving every program
    /// means the OPTIONS moved instead. The second case puts the parity claim above in question along with the
    /// goldens it licenses. There is no second producer left to check that against, so a bake here is now taken
    /// on the diff alone and the reading has to be that much more careful.
    /// </para>
    /// <para>
    /// THE SPIR-V IS DUMPED ON FAILURE to <c>Gpu/spirv-evidence/</c> as <c>.spv</c> files, for the same reason the
    /// Direct3D 11 test dumps its HLSL: with the modules in hand the two possible causes (the options moved, or
    /// the native emits differently on that runner's RID) are one <c>spirv-dis</c> apart rather than a guess.
    /// </para>
    /// </summary>
    public sealed class VulkanSpirvByteEqualityTests
    {
        /// <summary>Set to <c>1</c> to rewrite the checked-in table instead of asserting against it.</summary>
        const string UpdateEnvVar = "KE_UPDATE_SPIRV_HASHES";

        [Fact]
        public void EveryShippedProgramsSpirv_MatchesItsPinnedHash()
        {
            Dictionary<string, string> emitted = EmitEverything();

            if (IsUpdating())
            {
                WriteTable(emitted);
                return;
            }

            Dictionary<string, string> pinned = ReadTable();
            Assert.True(pinned.Count > 0,
                $"The pinned SPIR-V hash table at {TablePath()} is missing or empty. Bake it with "
                + $"{UpdateEnvVar}=1 and commit it. It is what decision V-T5 asserts against, so an absent table "
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
                "The compiled SPIR-V no longer matches its pinned hashes, which means either a shader source "
                + "changed, or the pinned front-end options changed (decision V-S2, "
                + $"{nameof(SpirvFrontEndPin)}). ONE program moving is a shader edit. EVERY program moving at once "
                + "is the options, and that is the drift this test exists to catch, because it invalidates the "
                + "one-off parity measurement that licenses the shared vulkan golden family carrying over. The "
                + "SPIR-V for each mismatch was written to Gpu/spirv-evidence/ so the change can be disassembled "
                + $"rather than guessed. Re-bake with {UpdateEnvVar}=1 once the change is understood and "
                + "intended.\n"
                + string.Join("\n", problems));
        }

        /// <summary>
        /// THE DEDUP IS A FACT ABOUT THE SHIPPED SET, asserted here rather than only in the cache's own unit test,
        /// because the number is what makes decision V-S7 worth having at all: 76 stage emissions collapse to 59
        /// distinct modules. That gap is real sharing (one fullscreen vertex source backs eleven post programs)
        /// and not an accident of naming, since the parity measurement established that the compile's label does
        /// not reach the bytes.
        /// <para>
        /// It is stated as an INEQUALITY on the distinct count plus an equality on the total, so adding a program
        /// that shares an existing stage does not fail a test about dedup for doing exactly what dedup is for. A
        /// distinct count that ROSE to meet the total would mean the sharing had gone, which is the regression
        /// worth catching.
        /// </para>
        /// </summary>
        [Fact]
        public void TheShippedProgramsShareStages_SoTheModuleCountIsBelowTheStageCount()
        {
            Dictionary<string, string> emitted = EmitEverything();
            int distinct = emitted.Values.Distinct(StringComparer.Ordinal).Count();

            // 78 since R5 added the tile-ground pair (the 76 in the note above is the 2026-08-08 measurement).
            Assert.Equal(78, emitted.Count);
            Assert.True(distinct < emitted.Count,
                $"The {emitted.Count} shipped stage emissions produced {distinct} distinct SPIR-V modules, so "
                + "nothing is shared and decision V-S7's dedup buys nothing. Measured at 59 distinct on "
                + "2026-08-08. A rise here means shipped sources that used to be identical have diverged, which "
                + "is worth reading rather than re-baking.");
        }

        // ---- emission ------------------------------------------------------------------------------------

        static Dictionary<string, string> EmitEverything()
        {
            var emitted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                emitted[program.Name + ".vertex"] = Sha256(
                    SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name));
                emitted[program.Name + ".fragment"] = Sha256(
                    SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name));
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                emitted[kernel.Name + ".compute"] = Sha256(
                    SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name));
            }
            return emitted;
        }

        // Hashed as the module's own bytes. That is BYTE equality of the emission, which is what V-T5 asks for and
        // what vkCreateShaderModule consumes: unlike the Direct3D 11 table's UTF-8 of a string, there is no text
        // encoding step in between here at all.
        static string Sha256(byte[] spirv) => Convert.ToHexStringLower(SHA256.HashData(spirv));

        static byte[] EmittedSpirv(string key)
        {
            int dot = key.LastIndexOf('.');
            string name = key.Substring(0, dot);
            string stage = key.Substring(dot + 1);

            foreach (ShippedGraphicsProgram program in D3D11ShaderProgramCatalog.GraphicsPrograms())
            {
                if (!string.Equals(program.Name, name, StringComparison.Ordinal)) continue;
                return stage == "vertex"
                    ? SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name)
                    : SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name);
            }
            foreach (ShippedComputeKernel kernel in D3D11ShaderProgramCatalog.ComputeKernels())
            {
                if (string.Equals(kernel.Name, name, StringComparison.Ordinal))
                    return SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name);
            }
            return Array.Empty<byte>();
        }

        // ---- the checked-in table ------------------------------------------------------------------------

        static bool IsUpdating()
        {
            string? value = Environment.GetEnvironmentVariable(UpdateEnvVar);
            return !string.IsNullOrWhiteSpace(value)
                && value.Trim() is "1" or "true" or "yes" or "on";
        }

        /// <summary>The table next to this source file, located by <see cref="CallerFilePathAttribute"/> so it
        /// does not depend on <c>dotnet test</c>'s working directory. Same shape the Direct3D 11 hash table and
        /// <c>GoldenCompare</c> both use.</summary>
        static string TablePath([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "spirv-hashes", "vulkan-spirv.sha256.txt");

        static string EvidenceDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "spirv-evidence");

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
            text.Append("# SHA-256 of the SPIR-V the front end emits for every shipped shader program, under\n")
                .Append("# the options pinned in KhaozEngine.Gpu/Internal/SpirvFrontEndPin.cs (decision V-S2).\n")
                .Append("# One line per emitted stage: <program>.<stage> <sha256>.\n")
                .Append("#\n")
                .Append("# These are the bytes vkCreateShaderModule receives verbatim on the native Vulkan\n")
                .Append("# backend, and the bytes the incumbent Veldrid Vulkan path received too: that equality\n")
                .Append("# was measured on 2026-08-08, 76 of 76 stages byte-identical, recorded in section 12.1\n")
                .Append("# of docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md, and was asserted on every\n")
                .Append("# leg by VulkanSpirvIncumbentParityTests until 18.0.0. This table is NEITHER: it is a\n")
                .Append("# DRIFT detector baked from this path's own emission.\n")
                .Append("#\n")
                .Append("# Re-bake with KE_UPDATE_SPIRV_HASHES=1 and read the diff. One program moving is a\n")
                .Append("# shader edit. Every program moving at once is the options, which puts the parity claim\n")
                .Append("# and the vulkan golden family it licenses in question: read the parity test's result\n")
                .Append("# before baking.\n")
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
                byte[] spirv = EmittedSpirv(key);
                if (spirv.Length == 0) return;
                string dir = EvidenceDir();
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, key + ".spv"), spirv);
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
